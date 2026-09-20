## ADDED Requirements

### Requirement: 模式切换复用官方登录并保留暂时失败前的模型

国内版与国际版切换 SHALL 通过对应官方 ACP 读取已有登录；成功授权可短时复用，失败或未授权结果 SHALL NOT 作为成功缓存复用。暂时不可用 SHALL NOT 清除已属于当前连接的模型；实际发送仍 SHALL 通过当前官方授权校验。

#### Scenario: 本机两种 WorkBuddy 均已登录

- **WHEN** 用户切换国内版和国际版
- **THEN** 分别取得对应账号的模型目录，无需点击获取或重新登录
- **AND THEN** 模式切换不调用 authenticate，不保存 WorkBuddy 令牌

#### Scenario: 短暂不可用后恢复

- **WHEN** 一次 ACP 探测不可用，随后用户切回该模式或刷新
- **THEN** 重新读取真实状态，不直接沿用失败缓存
- **AND THEN** 暂时不可用期间保留该连接的已选模型，明确未授权时清除
