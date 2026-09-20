# 设计

## 账号保护

源码 FileAuthenticationStorage.getAuthSavePath 使用 FilePathService.sharedDataPath/auth 与产品 authentication.id；CODEBUDDY_CONFIG_DIR 只改变普通配置，未隔离认证。禁止篡改产品认证 ID、复制凭据或把浏览器 cookie 作为替代方案。已有账号由官方客户端管理，ChatSheet 刷新其结果。

普通登录的门槛为：来源探测明确 Unauthorized，取得跨进程独占锁，重新 initialize/getUserInfo 确认没有 userInfo，再调用 authenticate。已授权、协议不支持和网络异常均不进入重新授权。账号切换打开已发现且与当前来源匹配的官方客户端，用户完成后主动刷新。

## 状态与来源

每种模式仅保存成功选择的产品来源标识，文件不保存账号 ID 或凭据；CLI 路径仍从固定安装位置发现。来源不可用时报告错误，不退到另一账号。缓存最近成功的模型与身份只供同一来源的待验证展示；发送对话必须获得本次授权成功。

登录操作保留界面快照。auth-url 通知、重开、取消均携带 operationId，桥只接受正在运行的操作。浏览器打开失败不终止 ACP 等待。操作结束后链接失效。

## 身份显示

从模型查询的同一 ACP 进程调用 getUserInfo，只按白名单提取 userId；其余字段尤其 token/accessToken 不进入返回 payload、日志或存储。标题栏使用已保存连接的设置快照，未保存的模式选择不改变当前渠道。账号操作完成后刷新实际连接摘要。

## 验证

用模拟 ACP 检查已登录账号永不收到 authenticate、异常不触发登录、取消和失败不改原状态；测试来源锁定与令牌字段过滤。真实 WebView2 检查窄宽面板、完整 tooltip、各接入模式切换和迟到回调。真实账号仅做读取，禁止为测试退出账号。
