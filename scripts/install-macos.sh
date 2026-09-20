#!/bin/sh
set -eu

cat <<'EOF'
ChatSheet macOS 安装检查

当前版本的 ChatSheet 是 Windows COM 加载项，依赖 Microsoft Excel for Windows、
.NET Framework 4.8 和 WebView2。Excel for Mac 不支持 COM 加载项，因此本脚本不会
伪装安装、修改 Excel 或写入系统配置。

如果你需要使用 ChatSheet，请在 Windows 10/11 的 Microsoft Excel 桌面版中安装：
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-online.ps1

macOS 原生支持需要改用 Office.js 加载项和跨平台本地服务，尚未包含在当前发行版。
EOF

if [ "${1:-}" = "--check" ]; then
  exit 0
fi
exit 2
