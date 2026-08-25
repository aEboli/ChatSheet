# 任务

## 安装器

- [x] `scripts/menu.ps1`：编号菜单（1 安装 / 2 卸载 / 3 诊断 / 4 退出），
      循环到用户选退出为止；非法输入提示后重新给选项
- [x] `menu.ps1` 启动时自我提权，避免 `install.ps1` 的 UAC 重启把菜单一起 `exit` 掉
- [x] `menu.ps1` 用 `&` 调用 `install.ps1`，不点源（点源会被它的 `exit` 带走）
- [x] `menu.ps1` 菜单头显示已装版本、运行模式（源码检出 / 预构建发行包）、安装位置
- [x] `menu.ps1` 用 try/catch 收住异常：一次失败不该让整个菜单退出
- [x] 根目录 `install.bat`：纯 ASCII、CRLF，只负责带 `-ExecutionPolicy Bypass`
      转发给 `menu.ps1`；找不到 `menu.ps1` 时给出可执行的提示
- [x] `menu.ps1` 存成带 BOM 的 UTF-8（PowerShell 5.1 无 BOM 会按 ANSI 读中文）

## 发行包

- [x] `package-release.ps1`：`menu.ps1` 进 `scripts\`，`install.bat` 进包根
      （与源码检出里根目录到 `scripts\` 的相对位置一致，同一个 `.bat` 通用）
- [x] `package-release.ps1`：两者加进 `$requiredPackageFiles` 校验

## 文档

- [x] README：ZIP 安装、源码安装、「安装、卸载与诊断」三处改为以双击入口为主
- [x] `docs/windows-release-install.md`：目录树加 `install.bat` 与 `menu.ps1`；
      安装步骤改为双击输入 1；第 3 节说明三件事都能在菜单里做
- [x] `openspec/specs/windows-release-distribution/spec.md`：新增
      「A double-clickable installer with numbered choices」要求

## 验证结果

- 菜单渲染、状态检测、输入 4 退出：通过（中文显示正常，编码无误）
- 非法输入（`9` 与空回车）：均提示后重新给选项，未退出
- 输入 3 诊断：在同一窗口跑完并返回菜单，证明 `&` 调用没被 `exit` 带走
- `package-release.ps1` 实跑：`install.bat` 在包根、`menu.ps1` 在 `scripts\`，
  两者都进了 `SHA256SUMS.txt`，`.bat` 仍为纯 ASCII、`menu.ps1` 仍带 BOM
- 打包后的副本单独运行：正确识别为「预构建发行包」，与仓库里的「源码检出」相区分
- 未跑：输入 1 / 2 会真正改注册表与安装目录，本次未在菜单里执行
  （安装路径本身已在上一次变更中实跑验证过）
