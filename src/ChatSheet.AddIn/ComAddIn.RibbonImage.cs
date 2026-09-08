using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ChatSheet.AddIn
{
    public sealed partial class ComAddIn
    {
        private static readonly object RibbonImageLock = new object();

        /// <summary>
        /// 已转换的图标。图片与 IPictureDisp 都要留着：
        /// Excel 会缓存 IPictureDisp，而它只是包住托管 Image，
        /// 后者被回收后功能区上的图标会变成空白。
        /// </summary>
        private static readonly Dictionary<string, CachedPicture> RibbonImages =
            new Dictionary<string, CachedPicture>(StringComparer.Ordinal);

        // ---- 每个按钮一个回调，名字必须与 Resources\Ribbon.xml 完全一致。
        //
        // 不做成「一个回调按 control.Id 分派」：那要经后期绑定去读 IRibbonControl.Id，
        // 读不到就只能返回 null，而返回 null 的表现正是「按钮上没有图标」——
        // 与图标资源缺失、与 XML 里回调名写错完全同一个症状，无从分辨。
        // 一个按钮一个方法则由 IReflect 的未知成员日志兜住（见 ComAddIn.Dispatch.cs）。 ----

        public object OnGetPaneImage(object control)
        {
            return RibbonImage("PanelLogo.png");
        }

        public object OnGetSettingsImage(object control)
        {
            return RibbonImage("RibbonSettings.png");
        }

        public object OnGetDiagnoseImage(object control)
        {
            return RibbonImage("RibbonDiagnose.png");
        }

        public object OnGetFitImage(object control)
        {
            return RibbonImage("RibbonFit.png");
        }

        public object OnGetUndoImage(object control)
        {
            return RibbonImage("RibbonUndo.png");
        }

        /// <summary>
        /// 按资源名取功能区图标，进程内只加载一次。
        /// 失败返回 null：功能区据此显示无图标的按钮，标签和说明仍在，
        /// 不影响点击，成因记在日志里。
        /// </summary>
        private static object RibbonImage(string fileName)
        {
            try
            {
                lock (RibbonImageLock)
                {
                    if (RibbonImages.TryGetValue(fileName, out var cached))
                    {
                        return cached.Picture;
                    }

                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceName = assembly.GetName().Name + ".Resources." + fileName;
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            Log.Error($"功能区图标资源缺失：{resourceName}", null);
                            return null;
                        }

                        // 复制一份再用：Image.FromStream 要求流在图片生命周期内保持打开，
                        // 而这里的流用完就关。
                        using (var source = Image.FromStream(stream))
                        {
                            var image = new Bitmap(source);
                            var entry = new CachedPicture(image, RibbonPictureConverter.FromImage(image));
                            RibbonImages[fileName] = entry;
                            return entry.Picture;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"加载功能区图标 {fileName} 失败", ex);
                return null;
            }
        }

        private sealed class CachedPicture
        {
            internal CachedPicture(Image image, object picture)
            {
                Image = image;
                Picture = picture;
            }

            /// <summary>只为保持存活，不再读取。</summary>
            internal Image Image { get; }

            internal object Picture { get; }
        }

        private sealed class RibbonPictureConverter : AxHost
        {
            private RibbonPictureConverter()
                : base(string.Empty)
            {
            }

            internal static object FromImage(Image image)
            {
                return GetIPictureDispFromPicture(image);
            }
        }
    }
}
