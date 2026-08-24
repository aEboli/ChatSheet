// OpenAI 兼容的最小 mock 服务，用于端到端验证而不消耗真实额度。
//
// 覆盖三条关键路径：
//   1. 模型列表   GET  /v1/models
//   2. 纯文本流式 POST /v1/chat/completions
//   3. 工具调用流式（分帧下发参数，验证增量拼接与工具执行）
//
// 用法：node server.mjs [端口] [场景]
//   场景 text   只回文本
//   场景 tool   先调用一个工具，收到结果后再回文本（默认）
//   场景 bulk   连续请求多次读取，快速堆高上下文，
//               用于验证 90% 阈值触发压缩的路径
//   场景 flaky  前两次以 503 拒绝再放行，验证失败重试确实会重来并最终成功
//   场景 reject 一律以 401 拒绝，验证配置类错误不被重试（只应收到一次请求）

import { createServer } from 'node:http';

const port = Number(process.argv[2]) || 58940;
const scenario = process.argv[3] || 'tool';

/** flaky 场景先失败几次。取 2 是为了既覆盖多次重试，又不必等满退避总时长。 */
const FLAKY_FAILURES = 2;

/** 对话请求计数。重试验证要靠它判断到底来了几次。 */
let chatAttempts = 0;

function sse(res, payload) {
  res.write(`data: ${JSON.stringify(payload)}\n\n`);
}

function textFrame(content) {
  return { choices: [{ delta: { content }, index: 0 }] };
}

function finish(reason) {
  return { choices: [{ delta: {}, index: 0, finish_reason: reason }] };
}

function usage(prompt, completion) {
  return { choices: [], usage: { prompt_tokens: prompt, completion_tokens: completion } };
}

const server = createServer((req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${port}`);
  // 日志一律英文：本进程输出被 PowerShell 重定向读取，
  // Node 写 UTF-8 而 PowerShell 按 ANSI 读，中文会成乱码。
  console.log(`[mock] ${req.method} ${url.pathname}`);

  if (url.pathname === '/v1/models') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ data: [{ id: 'mock-model' }, { id: 'mock-model-mini' }] }));
    return;
  }

  if (url.pathname !== '/v1/chat/completions') {
    res.writeHead(404).end('not found');
    return;
  }

  let body = '';
  req.on('data', (chunk) => { body += chunk; });
  req.on('end', () => {
    let parsed = {};
    try { parsed = JSON.parse(body); } catch {}

    const messages = parsed.messages ?? [];
    // 已经收到过工具结果，说明这是第二轮，该收尾了。
    const hasToolResult = messages.some((m) => m.role === 'tool');

    // 统计收到的图片：验证多模态载荷是否真的送达。
    let imageCount = 0;
    let imageTypes = [];
    for (const message of messages) {
      if (!Array.isArray(message.content)) { continue; }
      for (const block of message.content) {
        if (block?.type === 'image_url' && block.image_url?.url?.startsWith('data:')) {
          imageCount += 1;
          const match = /^data:([^;]+);base64,/.exec(block.image_url.url);
          if (match) { imageTypes.push(match[1]); }
        }
      }
    }

    console.log(
      `[mock] messages=${messages.length} hasToolResult=${hasToolResult} ` +
        `tools=${(parsed.tools ?? []).length} images=${imageCount}` +
        (imageTypes.length > 0 ? ` types=${imageTypes.join(',')}` : ''),
    );

    chatAttempts += 1;

    // 一律拒绝：401 属于配置错误，重试多少次都一样，
    // 因此加载项应当只请求一次就把错误报出来。
    if (scenario === 'reject') {
      console.log(`[mock] attempt ${chatAttempts} -> 401 (must not be retried)`);
      res.writeHead(401, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'invalid api key (mock)' } }));
      return;
    }

    // 先失败几次再放行：验证重试真的会重来，且带 Retry-After 时按它等待。
    if (scenario === 'flaky' && chatAttempts <= FLAKY_FAILURES) {
      console.log(`[mock] attempt ${chatAttempts} -> 503 (expect retry)`);
      res.writeHead(503, { 'Content-Type': 'application/json', 'Retry-After': '1' });
      res.end(JSON.stringify({ error: { message: 'upstream temporarily unavailable (mock)' } }));
      return;
    }

    if (scenario === 'flaky') {
      console.log(`[mock] attempt ${chatAttempts} -> 200 (recovered)`);
    }

    res.writeHead(200, {
      'Content-Type': 'text/event-stream; charset=utf-8',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
    });

    // bulk 场景：每轮都请求读取一大片范围，让工具结果不断累积。
    // 读取上限是 5000 个单元格，单次结果已相当可观，数轮即可逼近上下文预算。
    const toolResultCount = messages.filter((m) => m.role === 'tool').length;

    const send = async () => {
      // image 场景：只回文本并报出收到的图片数，用于验证多模态链路。
      if (scenario === 'image') {
        const reply = imageCount > 0
          ? `我收到了 ${imageCount} 张图片（${imageTypes.join('、')}）。`
          : '我没有收到任何图片。';

        for (const piece of reply.match(/.{1,6}/gs) ?? []) {
          await new Promise((r) => setTimeout(r, 30));
          sse(res, textFrame(piece));
        }

        sse(res, finish('stop'));
        sse(res, usage(150, 30));
        res.write('data: [DONE]\n\n');
        res.end();
        return;
      }

      if (scenario === 'bulk') {
        // 固定轮数后收尾，避免无限循环。
        if (toolResultCount < 12) {
          sse(res, textFrame(`第 ${toolResultCount + 1} 轮读取。`));
          await new Promise((r) => setTimeout(r, 60));

          // 每轮读不同范围，避免被上层去重。
          const startRow = toolResultCount * 40 + 1;
          const args = JSON.stringify({ range: `A${startRow}:T${startRow + 39}` });

          sse(res, {
            choices: [{
              index: 0,
              delta: {
                tool_calls: [{
                  index: 0,
                  id: `call_bulk_${toolResultCount}`,
                  type: 'function',
                  function: { name: 'read_range', arguments: args },
                }],
              },
            }],
          });

          sse(res, finish('tool_calls'));
          sse(res, usage(500 * (toolResultCount + 1), 60));
          res.write('data: [DONE]\n\n');
          res.end();
          return;
        }

        sse(res, textFrame('读取完毕。'));
        sse(res, finish('stop'));
        sse(res, usage(9000, 80));
        res.write('data: [DONE]\n\n');
        res.end();
        return;
      }

      if (scenario === 'tool' && !hasToolResult) {
        // 分帧下发工具调用参数，验证增量拼接。
        sse(res, textFrame('我先看一下工作簿结构。'));
        await new Promise((r) => setTimeout(r, 120));

        sse(res, {
          choices: [{
            index: 0,
            delta: {
              tool_calls: [{
                index: 0,
                id: 'call_mock_1',
                type: 'function',
                function: { name: 'write_values', arguments: '' },
              }],
            },
          }],
        });

        const argChunks = [
          '{"range"',
          ':"A1:B2"',
          ',"values":[["名称","数量"]',
          ',["铅笔",10]]}',
        ];
        for (const chunk of argChunks) {
          await new Promise((r) => setTimeout(r, 80));
          sse(res, {
            choices: [{
              index: 0,
              delta: { tool_calls: [{ index: 0, function: { arguments: chunk } }] },
            }],
          });
        }

        await new Promise((r) => setTimeout(r, 80));
        sse(res, finish('tool_calls'));
        sse(res, usage(120, 45));
        res.write('data: [DONE]\n\n');
        res.end();
        return;
      }

      // 收尾轮：逐字下发一段含 Markdown 的文本。
      const reply = '已写入 **A1:B2**。\n\n| 名称 | 数量 |\n| --- | --- |\n| 铅笔 | 10 |\n\n还需要我做什么？';
      for (const piece of reply.match(/.{1,6}/gs) ?? []) {
        await new Promise((r) => setTimeout(r, 40));
        sse(res, textFrame(piece));
      }

      sse(res, finish('stop'));
      sse(res, usage(200, 60));
      res.write('data: [DONE]\n\n');
      res.end();
    };

    void send();
  });
});

server.listen(port, '127.0.0.1', () => {
  console.log(`mock listening http://127.0.0.1:${port}/v1  scenario=${scenario}`);
});
