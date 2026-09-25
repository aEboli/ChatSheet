// 宿主相关的用户可见文案。Excel 与 Word 共用面板结构，
// 但欢迎语和附件建议必须跟随当前宿主，不能把表格操作提示带进 Word。

const WORD_HOSTS = new Set(['MicrosoftWord', 'WpsWriter', 'Word']);

export function isWordOfficeHost(info) {
  if (typeof info === 'boolean') {
    return info;
  }

  if (typeof info === 'string') {
    return WORD_HOSTS.has(info);
  }

  return WORD_HOSTS.has(info?.hostKind) || info?.hostMode === 'word';
}

export function welcomeCopy(info) {
  if (isWordOfficeHost(info)) {
    return {
      title: '我是 Office-helper，你的文档助手',
      body:
        '告诉我你想完成什么，我可以帮你读取和整理文档、修改文字和段落、编辑表格和设置页面，' +
        '直接在当前文档中操作。\n\n' +
        '**试试这样说**\n' +
        '- 把标题改成二级标题并统一格式\n' +
        '- 将选中的段落改写得更简洁\n' +
        '- 在第三段后插入一页分页符\n\n' +
        '需要补充资料时，可粘贴或拖入图片、文本文件。修改是否需要确认，' +
        '取决于下方选择的审批方式。',
    };
  }

  return {
    title: '我是 Office-helper，你的表格助手',
    body:
      '告诉我你想完成什么，我可以帮你整理数据、编写公式、调整格式和制作图表，' +
      '直接在当前工作簿中操作。\n\n' +
      '**试试这样说**\n' +
      '- 按销售额从高到低排序，保留标题行\n' +
      '- B 列是收入，C 列是成本，在 D 列计算毛利率\n' +
      '- 用 B 列的产品名称和 C 列的销售额生成柱状图\n\n' +
      '需要补充资料时，可粘贴或拖入图片、文本文件。修改是否需要确认，' +
      '取决于下方选择的审批方式。',
  };
}

export function binaryAttachmentHint(extension, info) {
  const normalized = String(extension ?? '').toLowerCase();
  if (isWordOfficeHost(info) && (normalized === '.docx' || normalized === '.doc')) {
    return '直接在 Word 或 WPS Writer 中打开它，我能读你当前打开的文档';
  }

  const hints = {
    '.xlsx': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
    '.xls': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
    '.xlsm': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
    '.docx': '可以另存为 txt 或 md 后再拖进来',
    '.doc': '可以另存为 txt 或 md 后再拖进来',
    '.pdf': '可以复制其中的文字直接粘贴到输入框',
    '.zip': '解压后把其中的文本文件拖进来',
  };
  return hints[normalized] ?? '';
}

export function encodingSaveHint(info) {
  return isWordOfficeHost(info)
    ? '请用记事本或 Word/WPS Writer 另存为 UTF-8 后再拖进来。'
    : '请用记事本或 Excel 另存为 UTF-8 后再拖进来。';
}
