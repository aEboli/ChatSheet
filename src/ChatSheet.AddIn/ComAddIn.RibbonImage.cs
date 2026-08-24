using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ChatSheet.AddIn
{
    public sealed partial class ComAddIn
    {
        private static readonly object PanelLogoLock = new object();
        private static Image _panelLogoImage;
        private static object _panelLogoPicture;

        /// <summary>
        /// 返回功能区“ChatSheet 面板”按钮的自定义图标。
        /// 图片对象需在 Excel 缓存期间保持存活，因此在进程内只加载一次。
        /// </summary>
        public object OnGetPaneImage(object control)
        {
            try
            {
                lock (PanelLogoLock)
                {
                    if (_panelLogoPicture != null)
                    {
                        return _panelLogoPicture;
                    }

                    var assembly = Assembly.GetExecutingAssembly();
                    var resourceName = assembly.GetName().Name + ".Resources.PanelLogo.png";
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            Log.Error($"功能区图标资源缺失：{resourceName}", null);
                            return null;
                        }

                        using (var source = Image.FromStream(stream))
                        {
                            _panelLogoImage = new Bitmap(source);
                        }
                    }

                    _panelLogoPicture = RibbonPictureConverter.FromImage(_panelLogoImage);
                    return _panelLogoPicture;
                }
            }
            catch (Exception ex)
            {
                Log.Error("加载功能区面板图标失败", ex);
                return null;
            }
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
