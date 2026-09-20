# 验证记录

- Release 构建成功，0 警告、0 错误。
- `ChatSheet.ToolTests.exe --workbuddy-account-tests` 全部通过，失败数 0。
- `ChatSheet.ToolTests.exe --workbuddy-fallback-live-intl` 返回已授权、21 个模型、20 个有倍率、3 个零倍率；ACP 会话实际接受全部 21 个模型的 `session/set_model`，未发送推理请求。
- 直接启动与 WPS 命令生成的宿主标识和快照路径一致；通用 WMI 管道初始化和回收测试通过。最终 WPS 界面尚待重新打开确认。
- 安装自检通过，安装与 Release DLL 的 SHA256 均为 `0430AC4B45182DFBB05244DF8BA856691775C06940726240058948C357456A47`。
- OpenSpec 提案严格校验通过。

## 限制

桌面快照由 WorkBuddy 更新；没有桌面安装或快照时仍使用当前官方 ACP 返回的实际目录，不保证不同官方产品本身拥有相同模型。未逐个测试模型生成回答。
