# macOS 安装检查

当前 ChatSheet 发行版是 Windows COM 加载项，不能加载到 Excel for Mac。仓库根目录的 install.command 会打开 scripts/install-macos.sh，输出支持边界并以非零状态结束，不会修改系统配置。

macOS 原生版本需要 Office.js 加载项和跨平台本地服务，尚未纳入当前 Release。请使用 Windows 10/11 的 Microsoft Excel 桌面版安装 Windows ZIP。
