using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace Grabber
{
    /// <summary>
    /// 用 WebView2 解码 mp4 并把指定时刻的帧导出成 PNG。
    ///
    /// 为什么走这条路：本机没有 ffmpeg，也没有 PIL/cv2/imageio，
    /// 而 WebView2 自带 H.264 解码。把视频所在目录映射成虚拟主机，
    /// 页面里用 <video> 定位时刻、canvas.drawImage 取帧、toDataURL 导出。
    ///
    /// 用法：
    ///   Grabber.exe 视频路径 输出目录 [起始秒] [结束秒] [帧数]
    ///   Grabber.exe 视频路径 输出目录 --probe        只报时长与分辨率
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            if (args.Length < 2)
            {
                Console.WriteLine("用法：Grabber.exe 视频路径 输出目录 [起始秒 结束秒 帧数]");
                Console.WriteLine("      Grabber.exe 视频路径 输出目录 --probe");
                return 2;
            }

            var video = Path.GetFullPath(args[0]);
            var outDir = Path.GetFullPath(args[1]);
            var probeOnly = Array.Exists(args, a => a == "--probe");
            // 列出真实帧的时刻。按 seek 取帧无法知道帧边界在哪，
            // 于是「相邻两个采样是不是同一帧」只能靠猜，位移曲线就会读错。
            // requestVideoFrameCallback 在每帧呈现时给出 mediaTime，那是权威值。
            var framesOnly = Array.Exists(args, a => a == "--frames");

            var from = args.Length > 3 && double.TryParse(args[2], out var f) ? f : 0.0;
            var to = args.Length > 3 && double.TryParse(args[3], out var t) ? t : 0.0;
            var count = args.Length > 4 && int.TryParse(args[4], out var c) ? c : 0;

            if (!File.Exists(video))
            {
                Console.WriteLine("找不到视频：" + video);
                return 2;
            }

            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();

            var exit = 0;

            using (var form = new Form
            {
                Text = "取帧",
                Width = 900,
                Height = 700,
                StartPosition = FormStartPosition.CenterScreen,
            })
            {
                var view = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(view);

                form.Shown += async (s, e) =>
                {
                    try
                    {
                        await view.EnsureCoreWebView2Async();

                        var dir = Path.GetDirectoryName(video);
                        view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "refs.local", dir,
                            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                        var name = Path.GetFileName(video);
                        var html =
                            "<!doctype html><html><body style='margin:0;background:#222'>" +
                            $"<video id='v' src='https://refs.local/{name}' " +
                            "style='max-width:100%' muted playsinline></video>" +
                            "<canvas id='c' style='display:none'></canvas>" +
                            "<script>" +
                            "const v=document.getElementById('v');" +
                            "const c=document.getElementById('c');" +
                            "window.__ready=false;" +
                            "v.addEventListener('loadeddata',()=>{window.__ready=true;});" +
                            "v.addEventListener('error',()=>{window.__err='video error';});" +
                            // 定位到某个时刻并把该帧导出为 dataURL。
                            // ExecuteScriptAsync 不等 Promise，所以结果挂到 window 上轮询取。
                            "window.grab=function(time){" +
                            "  window.__frame=null;" +
                            "  const onSeek=()=>{" +
                            "    v.removeEventListener('seeked',onSeek);" +
                            "    c.width=v.videoWidth; c.height=v.videoHeight;" +
                            "    c.getContext('2d').drawImage(v,0,0);" +
                            "    window.__frame=c.toDataURL('image/png');" +
                            "  };" +
                            "  v.addEventListener('seeked',onSeek);" +
                            "  v.currentTime=time;" +
                            "};" +
                            "</script></body></html>";

                        view.CoreWebView2.NavigateToString(html);

                        // 等视频元数据就绪。
                        var ready = false;
                        for (var i = 0; i < 60; i++)
                        {
                            await Task.Delay(250);
                            var r = await view.CoreWebView2.ExecuteScriptAsync("window.__ready === true");
                            if (r == "true") { ready = true; break; }
                            var err = await view.CoreWebView2.ExecuteScriptAsync("window.__err || ''");
                            if (err.Contains("error")) { break; }
                        }

                        if (!ready)
                        {
                            Console.WriteLine("视频未能就绪（解码失败或路径映射不通）");
                            exit = 1;
                            return;
                        }

                        var durRaw = await view.CoreWebView2.ExecuteScriptAsync("v.duration");
                        var wRaw = await view.CoreWebView2.ExecuteScriptAsync("v.videoWidth");
                        var hRaw = await view.CoreWebView2.ExecuteScriptAsync("v.videoHeight");
                        Console.WriteLine($"时长={durRaw}s 分辨率={wRaw}x{hRaw}");

                        if (probeOnly) { return; }

                        if (framesOnly)
                        {
                            // 播一遍并记下每帧的 mediaTime。取帧靠 seek 时无从知道
                            // 帧边界，这里拿到的是权威的帧时刻表。
                            await view.CoreWebView2.ExecuteScriptAsync(
                                "window.__times=[];" +
                                "(function tick(){" +
                                "  v.requestVideoFrameCallback((now,meta)=>{" +
                                "    window.__times.push(meta.mediaTime);" +
                                "    tick();" +
                                "  });" +
                                "})();" +
                                "v.playbackRate=1; v.play();");

                            for (var i = 0; i < 80; i++)
                            {
                                await Task.Delay(250);
                                var ended = await view.CoreWebView2.ExecuteScriptAsync("v.ended");
                                if (ended == "true") { break; }
                            }

                            var times = await view.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify(window.__times)");
                            var listPath = Path.Combine(outDir, "frame-times.json");
                            File.WriteAllText(listPath, times);

                            var parts = times.Trim('"').Replace("\\", "").Trim('[', ']').Split(',');
                            Console.WriteLine($"共 {parts.Length} 帧，已写入 {listPath}");
                            if (parts.Length > 2 &&
                                double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var t0) &&
                                double.TryParse(parts[parts.Length - 1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var tn))
                            {
                                var fps = (parts.Length - 1) / (tn - t0);
                                Console.WriteLine($"首帧 {t0:0.000}s 末帧 {tn:0.000}s → 平均 {fps:0.00} fps" +
                                    $"（帧间隔 {1000 / fps:0.0}ms）");
                            }
                            return;
                        }

                        double.TryParse(durRaw, out var duration);
                        if (to <= 0) { to = duration; }
                        if (count <= 0) { count = 40; }

                        var step = count > 1 ? (to - from) / (count - 1) : 0;
                        for (var i = 0; i < count; i++)
                        {
                            var at = from + step * i;
                            await view.CoreWebView2.ExecuteScriptAsync(
                                $"window.grab({at.ToString(System.Globalization.CultureInfo.InvariantCulture)})");

                            string frame = null;
                            for (var w = 0; w < 40; w++)
                            {
                                await Task.Delay(50);
                                var got = await view.CoreWebView2.ExecuteScriptAsync("window.__frame");
                                if (got != null && got.Length > 20 && got != "null")
                                {
                                    frame = got;
                                    break;
                                }
                            }

                            if (frame == null)
                            {
                                Console.WriteLine($"[{i}] {at:0.000}s 取帧超时");
                                continue;
                            }

                            // ExecuteScriptAsync 返回 JSON 字面量，字符串带引号与转义。
                            var dataUrl = frame.Trim('"').Replace("\\/", "/");
                            var comma = dataUrl.IndexOf(',');
                            if (comma < 0) { continue; }

                            var bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
                            var path = Path.Combine(outDir, $"f{i:D3}_{(int)Math.Round(at * 1000)}ms.png");
                            File.WriteAllBytes(path, bytes);
                            Console.WriteLine($"[{i}] {at:0.000}s -> {Path.GetFileName(path)} ({bytes.Length} 字节)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("失败：" + ex);
                        exit = 1;
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }

            return exit;
        }
    }
}
