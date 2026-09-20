# 验证记录

## 根因和复现

本机官方源码 C:/Program Files/WorkBuddyAI/resources/app.asar.unpacked/cli/dist/codebuddy.js 中，authenticate 在已有 currentSession 时调用 logout；FileAuthenticationStorage.getAuthSavePath 使用 sharedDataPath/auth 和产品认证 ID，配置目录不能隔离该登出。CliExternalUriOpener.open 先发布 URL 通知再调用 UrlUtils.openUrl；原 ChatSheet 收到同一通知再次开窗。

模拟 ACP 覆盖已有授权、模型探测异常、用户信息已有会话、协议不支持、正常完成、浏览器授权成功而 authenticate 不返回、重复 URL、取消与多个面板竞争。不使用真实账号执行退出式授权。

## 自动验证

- Release 整个解决方案构建：零警告、零错误。
- --workbuddy-login-tests：15 项通过。
- --workbuddy-account-tests 与 --workbuddy-tests：全部通过，含 WMI 命名管道进程回收。
- tests/web 的 33 个测试文件全部通过；设置页事件回归 41 项通过，包含忙碌状态恢复、操作标识、迟到回调、取消、来源模式切换和返回窗口刷新。
- 真实 WebView2 360px 面板：全部通过；读取实际国内/国际目录和切回国际模式，账号切换请求由测试拦截，未触发真实登出。
- 真实国际 ACP 只读查询：Authorized，17 个模型，当前来源 .codebuddy。
- 修改文件通过 UTF-8 解码及异常替换字符检查，git diff --check 通过。

## 产物和安装

构建版本为 0.10.3.11。artifacts/release/ChatSheet-v0.10.3.11-win.zip 的外部 SHA-256 与包内 32 个文件校验全部通过。

初次检查发现 WPS 占用旧 DLL；用户确认保存并完全退出 WPS 后，使用 scripts/install.ps1 -Action install -SkipBuild 安装成功。x86/x64 COM 类、面板类及 Excel/WPS 登记自检通过；安装目录中程序集版本为 0.10.3.11，DLL、settings.js、index.html 的 SHA-256 均与构建产物一致。

## 边界

未替用户执行 Google/GitHub 退出、重新登录或真实账号切换；这些第三方交互由用户完成。已验证应用对官方协议通知、成功状态和超时/取消的处理。只有独立 CLI 来源时，不猜测或打开另一产品的桌面账号，而提示用户在当前官方来源管理账号。既有 workbuddy-session-safety 提案的标题栏身份与持久化来源功能不属于本次修复。
