// 主题切换的行为检查。
//
// theme.test.mjs 锁的是「有没有适配」，本文件锁的是「切换本身对不对」。
// 这些行为出错都不会报错，只会表现为反直觉的小毛病，而且都难复现：
//   · 手动选了浅色，第二天系统进夜间模式，面板自己变深了；
//   · 切换一次生效、重开面板又回到系统色（存档没写进去）；
//   · localStorage 不可用时整个脚本抛异常，连页面都是白板。
//
// theme.js 是普通脚本（必须在首屏前同步执行，见该文件头部），没有导出，
// 所以这里把它当源码跑：造一套最小的 window / document 替身注入进去，
// 再从 window.chatSheetTheme 与替身上的痕迹检查它做了什么。
// 这样测的是真实源码而不是它的复制品。
//
// 运行：node tests/web/theme-switch.test.mjs

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(
  join(here, '..', '..', 'src', 'web', 'scripts', 'theme.js'),
  'utf8',
);

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

/**
 * 造一个运行环境并把 theme.js 跑起来。
 *
 * stored 为已存下的选择（''表示没存过），systemDark 为系统偏好，
 * storageBroken 模拟 localStorage 不可用（无源上下文里读写会直接抛）。
 */
function boot({ stored = '', systemDark = false, storageBroken = false } = {}) {
  const store = new Map();
  if (stored) { store.set('chatsheet.theme', stored); }

  const button = {
    title: '',
    attrs: {},
    clicks: [],
    setAttribute(name, value) { this.attrs[name] = value; },
    addEventListener(type, handler) { this.clicks.push([type, handler]); },
    click() {
      for (const [type, handler] of this.clicks) {
        if (type === 'click') { handler(); }
      }
    },
  };

  const documentElement = {
    attrs: {},
    setAttribute(name, value) { this.attrs[name] = value; },
  };

  const docListeners = new Map();
  // 按钮在 DOMContentLoaded 之前取不到：脚本跑在 <head> 里，那时 <body> 还没解析。
  let domReady = false;

  const doc = {
    documentElement,
    getElementById: (id) => (domReady && id === 'theme-toggle' ? button : null),
    addEventListener: (type, handler) => { docListeners.set(type, handler); },
  };

  const mediaListeners = [];
  const win = {
    localStorage: {
      getItem: (key) => {
        if (storageBroken) { throw new Error('localStorage 不可用'); }
        return store.has(key) ? store.get(key) : null;
      },
      setItem: (key, value) => {
        if (storageBroken) { throw new Error('localStorage 不可用'); }
        store.set(key, value);
      },
    },
    matchMedia: (query) => ({
      matches: query.includes('dark') ? systemDark : false,
      addEventListener: (type, handler) => { mediaListeners.push(handler); },
    }),
  };

  // theme.js 只通过 window / document 接触外界，注入这两个就够。
  new Function('window', 'document', source)(win, doc);

  return {
    button,
    store,
    theme: () => documentElement.attrs['data-theme'],
    api: () => win.chatSheetTheme,
    fireDomReady: () => {
      domReady = true;
      docListeners.get('DOMContentLoaded')?.();
    },
    /** 模拟系统主题变化。 */
    setSystemDark: (value) => {
      win.matchMedia = (query) => ({
        matches: query.includes('dark') ? value : false,
        addEventListener: () => {},
      });
      for (const handler of mediaListeners) { handler(); }
    },
  };
}

console.log('检查首屏定主题：');
console.log('');

{
  const app = boot({ systemDark: false });
  check('没存过选择时跟随系统（浅色）', app.theme() === 'light', `实际 ${app.theme()}`);
}

{
  const app = boot({ systemDark: true });
  check('没存过选择时跟随系统（深色）', app.theme() === 'dark', `实际 ${app.theme()}`);
}

{
  const app = boot({ stored: 'dark', systemDark: false });
  check('存过的选择优先于系统偏好', app.theme() === 'dark', `实际 ${app.theme()}`);
}

{
  const app = boot({ stored: 'light', systemDark: true });
  check('存过浅色时不被系统的深色顶掉', app.theme() === 'light', `实际 ${app.theme()}`);
}

{
  // 存了个不认识的值（手改过、或旧版本留下的），应当退回跟随系统而不是照用。
  const app = boot({ stored: 'midnight', systemDark: true });
  check('存了无法识别的值时退回跟随系统', app.theme() === 'dark', `实际 ${app.theme()}`);
}

{
  const app = boot({ systemDark: true, storageBroken: true });
  check('localStorage 不可用时仍能定出主题', app.theme() === 'dark', `实际 ${app.theme()}`);
}

console.log('');
console.log('检查点击切换：');
console.log('');

{
  const app = boot({ systemDark: false });
  app.fireDomReady();
  app.button.click();

  check('浅色下点一次变深色', app.theme() === 'dark', `实际 ${app.theme()}`);
  check('切换结果写进了存档', app.store.get('chatsheet.theme') === 'dark',
    `实际 ${app.store.get('chatsheet.theme')}`);

  app.button.click();
  check('再点一次回到浅色', app.theme() === 'light', `实际 ${app.theme()}`);
  check('存档跟着回到浅色', app.store.get('chatsheet.theme') === 'light',
    `实际 ${app.store.get('chatsheet.theme')}`);
}

{
  // 按钮文案说的是「点了会变成什么」，与显示的图标同指一个方向。
  const app = boot({ systemDark: false });
  app.fireDomReady();

  check('浅色下悬停说明指向深色', app.button.title === '切换到深色主题',
    `实际「${app.button.title}」`);
  check('浅色下 aria-label 与 title 一致',
    app.button.attrs['aria-label'] === '切换到深色主题',
    `实际「${app.button.attrs['aria-label']}」`);

  app.button.click();
  check('切到深色后说明反过来指向浅色', app.button.title === '切换到浅色主题',
    `实际「${app.button.title}」`);
}

{
  // 存不下不该妨碍本次切换：页面上的主题仍要变。
  const app = boot({ systemDark: false, storageBroken: true });
  app.fireDomReady();
  app.button.click();
  check('存档不可用时点击仍能切换', app.theme() === 'dark', `实际 ${app.theme()}`);
}

console.log('');
console.log('检查系统主题变化的跟随规则：');
console.log('');

{
  const app = boot({ systemDark: false });
  app.setSystemDark(true);
  check('没手动选过时跟随系统变深', app.theme() === 'dark', `实际 ${app.theme()}`);
}

{
  const app = boot({ systemDark: false });
  app.fireDomReady();
  app.button.click();
  // 此时用户已显式选了深色。系统转浅色不该把它拉回去。
  app.setSystemDark(false);
  check('手动选过之后系统变化不再覆盖', app.theme() === 'dark', `实际 ${app.theme()}`);
}

console.log('');
console.log('检查供模块侧使用的接口：');
console.log('');

{
  const app = boot({ systemDark: true });
  check('暴露了 chatSheetTheme', typeof app.api() === 'object');
  check('current 读出当前主题', app.api().current() === 'dark', `实际 ${app.api()?.current()}`);

  const seen = [];
  app.api().subscribe((theme) => seen.push(theme));
  check('subscribe 注册时立即回调一次', seen.length === 1 && seen[0] === 'dark',
    `实际 ${JSON.stringify(seen)}`);

  app.fireDomReady();
  app.button.click();
  check('切换时通知订阅方', seen.length === 2 && seen[1] === 'light',
    `实际 ${JSON.stringify(seen)}`);
}

{
  // 订阅方抛异常不该连累主题切换——它只是个旁听者。
  const app = boot({ systemDark: false });
  app.api().subscribe(() => { throw new Error('订阅方自己坏了'); });
  app.fireDomReady();

  let crashed = false;
  try {
    app.button.click();
  } catch (error) {
    crashed = true;
  }

  check('订阅方抛异常不影响切换', !crashed && app.theme() === 'dark',
    crashed ? '异常穿透到了切换流程' : `实际 ${app.theme()}`);
}

console.log('');
console.log(`=== 主题切换：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
