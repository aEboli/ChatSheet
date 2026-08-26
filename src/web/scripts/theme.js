// 主题（浅色 / 深色）的解析、应用与切换。
//
// 为什么是普通脚本而不是 ES 模块：这段必须在首屏绘制之前跑完。
// index.html 末尾的 app.js 带 type="module"，模块一律延迟到文档解析完才执行，
// 那时浅色底已经画出来了——深色下开面板会先闪一下白。所以本文件以同步
// <script> 放在 <head> 里，在 <body> 存在之前就把 data-theme 写到 <html> 上。
//
// 因此这里不能 import 任何东西，也不直接与加载项通信（消息桥是模块）。
// 需要联动的一方通过 window.chatSheetTheme 订阅，见 app.js。

(function () {
  'use strict';

  var STORAGE_KEY = 'chatsheet.theme';
  var root = document.documentElement;
  var listeners = [];

  /**
   * 读取用户存下的选择。
   * 返回 'light' / 'dark'，没存过则返回空——空表示「跟随系统」。
   *
   * try 是必须的：localStorage 在无源上下文（例如直接用 file:// 打开这个页面
   * 做静态检查）会直接抛，届时应当退回跟随系统而不是让整页脚本挂掉。
   */
  function stored() {
    try {
      var value = window.localStorage.getItem(STORAGE_KEY);
      return value === 'light' || value === 'dark' ? value : '';
    } catch (error) {
      return '';
    }
  }

  function store(theme) {
    try {
      window.localStorage.setItem(STORAGE_KEY, theme);
    } catch (error) {
      // 存不下只是下次打开回到跟随系统，本次切换仍然生效。
    }
  }

  function systemPrefersDark() {
    return Boolean(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  }

  /** 当前生效的主题：存过就用存的，否则跟随系统。 */
  function resolve() {
    return stored() || (systemPrefersDark() ? 'dark' : 'light');
  }

  /**
   * 把主题落到 <html data-theme> 上。
   *
   * 显式写死 light 或 dark，不留「auto」这个值：样式表里只有一套深色变量块
   * （:root[data-theme='dark']），跟随系统的判断在这里做完。否则深色调色板
   * 要在 prefers-color-scheme 媒体查询里再写一遍，两处迟早会写歪。
   */
  function apply(theme) {
    root.setAttribute('data-theme', theme);
    syncToggle(theme);

    for (var i = 0; i < listeners.length; i += 1) {
      notify(listeners[i], theme);
    }
  }

  /**
   * 通知一个订阅方。
   *
   * 订阅方自己抛了异常不该影响主题切换——它只是个旁听者，主题该切还是要切。
   * 注册时的那次立即回调也必须走这里：不然订阅方一抛，异常会从
   * subscribe() 穿回调用它的那一行，把调用方的初始化流程打断。
   */
  function notify(handler, theme) {
    try {
      handler(theme);
    } catch (error) {
      // 无处上报（此处拿不到消息桥），咽掉即可。
    }
  }

  /**
   * 更新切换按钮的悬停说明。
   *
   * 按钮显示的是「点了会变成什么」（浅色下显示月亮），所以文案也照目标写，
   * 图标与说明指向同一件事。显示当前状态的话，图标是月亮、说明写「深色」，
   * 而点下去反而变深色，两种读法互相矛盾。
   *
   * 图标本身由 CSS 按 data-theme 显示其中一个，这里不碰 DOM 结构。
   */
  function syncToggle(theme) {
    var button = document.getElementById('theme-toggle');
    if (!button) {
      return;
    }

    var label = theme === 'dark' ? '切换到浅色主题' : '切换到深色主题';
    button.title = label;
    button.setAttribute('aria-label', label);
  }

  function toggle() {
    var next = resolve() === 'dark' ? 'light' : 'dark';
    store(next);
    apply(next);
    return next;
  }

  // 首屏之前先定主题。此刻 <body> 还不存在，只写 <html> 的属性，
  // 按钮的文案等 DOM 就绪后补。
  apply(resolve());

  document.addEventListener('DOMContentLoaded', function () {
    syncToggle(resolve());

    var button = document.getElementById('theme-toggle');
    if (button) {
      button.addEventListener('click', toggle);
    }
  });

  // 系统主题变化时跟着走，但只在用户没做过显式选择时——
  // 手动选过就以他的选择为准，不该被系统的夜间模式覆盖掉。
  if (window.matchMedia) {
    var query = window.matchMedia('(prefers-color-scheme: dark)');
    var onSystemChange = function () {
      if (!stored()) {
        apply(resolve());
      }
    };

    // addEventListener 是标准写法；addListener 是给旧 WebView2 运行时的兜底。
    if (query.addEventListener) {
      query.addEventListener('change', onSystemChange);
    } else if (query.addListener) {
      query.addListener(onSystemChange);
    }
  }

  /**
   * 供模块侧使用的接口。
   * current 读当前主题，subscribe 订阅变化（注册时立即回调一次，
   * 省掉调用方自己先读一遍当前值）。
   */
  window.chatSheetTheme = {
    current: resolve,
    subscribe: function (handler) {
      listeners.push(handler);
      notify(handler, resolve());
    },
  };
})();
