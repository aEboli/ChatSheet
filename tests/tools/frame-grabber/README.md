# 参考视频取帧与像素分析

照着参考视频定动效参数时用的工具。不在解决方案里：它只服务于「量出视频里的动画
参数」这件事，不是产品的一部分，也不参与 CI。

## 为什么需要它

开发环境读不了图片，也没有 ffmpeg、PIL、cv2、imageio。而 WebView2 自带 H.264 解码，
`zlib` 是 Node 的标准库、PNG 的 IDAT 就是 zlib 流——于是「看视频」可以变成
「解码 PNG 自己算」。`v0.7.0` 里禁用按钮的抖动参数就是这么量出来的。

## 用法

```powershell
# 一、报时长与分辨率
dotnet build -c Release
.\bin\Release\net48\Grabber.exe 视频.mp4 输出目录 --probe

# 二、列出真实帧的时刻（先做这一步，别按固定步长盲取）
.\bin\Release\net48\Grabber.exe 视频.mp4 输出目录 --frames

# 三、按帧中心取样：起始秒 结束秒 帧数
.\bin\Release\net48\Grabber.exe 视频.mp4 输出目录 1.44167 1.79167 22
```

```bash
# 找动画区间
node analyse.mjs 帧目录 diff
# 变化区域的包围盒与重心（定位动画元素的坐标）
node analyse.mjs 帧目录 where
# 沿横向扫描线取亚像素左右边缘 + 弦宽（量位移，弦宽不变即纯平移）
node analyse.mjs 帧目录 edge 350 850,1260
# 竖向扫描线（查有没有上下位移）
node analyse.mjs 帧目录 vedge 1060 250,800
# 区域平均色（查有没有变色）
node analyse.mjs 帧目录 color 980,320,60,50
# 渲染成字符画（看清元素长什么样）
node analyse.mjs 帧目录 ascii f000_1300ms.png 760,280,600,500 100
```

## 三条经验

- **先问帧率，别按固定步长 seek 取帧。** `video.requestVideoFrameCallback` 的
  `meta.mediaTime` 是权威帧时刻。不知道帧边界时，「相邻两个采样是不是同一帧」只能猜，
  位移序列会读成乱跳的——第一次量抖动就栽在这里，误判成 30Hz 的高频振动。
- **定位元素用变化像素的包围盒**（`where`），别在字符画上目测坐标。
- **量位移用扫描线边缘 + 两侧灰度线性插值到亚像素，并同时报弦宽。** 弦宽不变就说明
  是纯平移、没有缩放，一条线同时答两个问题。扫描线要挑在鼠标指针之外，指针自己也在动。

## 两个环境坑

- `ExecuteScriptAsync` 不等 Promise，异步结果要挂到 `window` 上再轮询取。
- net48 的 AnyCPU 默认起 32 位进程，原生 `WebView2Loader.dll` 会位数不匹配报
  `0x8007000B`（映像格式错误，与真实原因毫无关系），因此 csproj 里显式
  `<PlatformTarget>x64</PlatformTarget>`。

参考视频本身不入库（第三方素材），放在被忽略的 `work/` 下即可。
