// 面板脚本的导入完整性检查。
//
// 存在的理由：使用了未导入的标识符会在运行时抛 ReferenceError，
// 而这类调用常位于 try 块内被 catch 吞掉，症状表现为「某个功能悄悄失效」
// 而非明确报错。实际已经踩过一次：chat.js 用了 logToHost 却没导入，
// 导致上下文圆环始终不更新且无任何日志。
//
// 同时检查未使用的导入，避免改动后留下死代码。
//
// 运行：node tests/web/imports.test.mjs

import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const scriptsDir = join(here, '..', '..', 'src', 'web', 'scripts');

let passed = 0;
let failed = 0;

function check(label, condition, detail = '') {
  if (condition) {
    passed += 1;
    console.log(`  通过  ${label}`);
  } else {
    failed += 1;
    console.log(`  失败  ${label}`);
    if (detail) { console.log(`        ${detail}`); }
  }
}

/** 收集某文件导出的名字。 */
function collectExports(source) {
  const names = new Set();
  for (const m of source.matchAll(/export\s+(?:async\s+)?function\s+([A-Za-z_$][\w$]*)/g)) {
    names.add(m[1]);
  }
  for (const m of source.matchAll(/export\s+(?:const|let|var)\s+([A-Za-z_$][\w$]*)/g)) {
    names.add(m[1]);
  }
  return names;
}

/** 收集某文件导入的名字。 */
function collectImports(source) {
  const names = new Set();
  for (const m of source.matchAll(/import\s*\{([^}]+)\}\s*from/g)) {
    for (const part of m[1].split(',')) {
      const name = part.trim().split(/\s+as\s+/).pop().trim();
      if (name) { names.add(name); }
    }
  }
  return names;
}

/** 判断标识符是否在源码中作为函数被调用（排除导入语句本身与属性访问）。 */
function isCalled(source, name) {
  const body = source.replace(/import[\s\S]*?from\s*'[^']+';?/g, '');
  // 前面不能紧跟单词字符或点号，否则会误匹配 obj.name 或 xxxname
  const pattern = new RegExp(`(^|[^\\w.$])${name}\\s*\\(`, 'm');
  return pattern.test(body);
}

/** 判断标识符是否在源码中被引用（不限于调用）。 */
function isReferenced(source, name) {
  const body = source.replace(/import[\s\S]*?from\s*'[^']+';?/g, '');
  const pattern = new RegExp(`(^|[^\\w.$])${name}([^\\w$]|$)`, 'm');
  return pattern.test(body);
}

const files = readdirSync(scriptsDir).filter((f) => f.endsWith('.js'));
const sources = new Map();
const exportsByFile = new Map();

for (const file of files) {
  const source = readFileSync(join(scriptsDir, file), 'utf8');
  sources.set(file, source);
  exportsByFile.set(file, collectExports(source));
}

console.log(`检查 ${files.length} 个脚本：${files.join('、')}`);
console.log('');

// 所有模块导出的名字汇总，用于判断某个调用是否来自其他模块。
const allExports = new Map();
for (const [file, names] of exportsByFile) {
  for (const name of names) {
    allExports.set(name, file);
  }
}

for (const file of files) {
  const source = sources.get(file);
  const imported = collectImports(source);
  const ownExports = exportsByFile.get(file);

  // 一、使用了其他模块的导出但没导入
  const missing = [];
  for (const [name, fromFile] of allExports) {
    if (fromFile === file) { continue; }
    if (ownExports.has(name)) { continue; }
    // 本文件内自己定义了同名函数时不算缺失
    const definedLocally = new RegExp(
      `(function|const|let|var)\\s+${name}\\b`,
    ).test(source);
    if (definedLocally) { continue; }

    if (isCalled(source, name) && !imported.has(name)) {
      missing.push(`${name}（来自 ${fromFile}）`);
    }
  }

  check(
    `${file} 无缺失的导入`,
    missing.length === 0,
    missing.length > 0 ? `缺少：${missing.join('、')}` : '',
  );

  // 二、导入了但从未使用
  const unused = [];
  for (const name of imported) {
    if (!isReferenced(source, name)) {
      unused.push(name);
    }
  }

  check(
    `${file} 无未使用的导入`,
    unused.length === 0,
    unused.length > 0 ? `未使用：${unused.join('、')}` : '',
  );
}

console.log('');
console.log(`=== 导入检查：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
