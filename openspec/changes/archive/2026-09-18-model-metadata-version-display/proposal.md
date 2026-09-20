# 提案：完善 WorkBuddy 模型显示与版本标识

## Why

WorkBuddy 已返回模型显示名称与倍率，但设置页只显示名称，且两个同名模型难以区分。
切换到 WorkBuddy 后，设置页顶部还会沿用旧接入模式的就绪信息；窄面板中的“自动获取”
按钮也会被拆成两行。标题栏缺少当前加载项版本，用户无法确认是否已安装更新。

## What Changes

- 解析 WorkBuddy 返回的有限倍率并在模型名称后显示，重复名称追加模型 ID。
- 保持模型 ID 为实际保存值，显示名称与倍率只作为 UI 文本。
- 切换接入模式时清理旧就绪信息，并让 WorkBuddy 模式显示自己的状态边界。
- 在标题栏显示使用楷体的中文大写版本号，并把本次版本更新为 `0.10.1`。
- 记录普通、大版本和超大版本的版本递增规则。

## Non-Goals

- 不改变 WorkBuddy 凭据存储或普通对话 ACP 桥接范围。
- 不把完整模型元数据写入 ChatSheet 设置文件或模型目录缓存。
- 不创建或伪造 GitHub `v0.10.1` 发行包。

## Capabilities

### New Capabilities

- `model-metadata-version-display`: 显示 WorkBuddy 模型倍率、修复模式切换状态并标识加载项版本。

## Impact

修改 WorkBuddy ACP 安全字段投影、设置页模型选项与状态渲染、标题栏静态资源和项目版本元数据；
增加前端与 provider 回归测试，并重新安装本地加载项。
