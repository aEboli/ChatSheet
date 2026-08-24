using System;

namespace ChatSheet.AddIn
{
    /// <summary>
    /// 面板宽度换算。
    ///
    /// 单独成类是为了能直接验证：这段算术曾经算错过，
    /// 而它依赖的窗格对象只有在真实宿主里才存在，混在一起就无法测。
    /// </summary>
    internal static class PaneWidthMath
    {
        /// <summary>
        /// 由目标 CSS 宽度算出宿主应设的宽度。
        ///
        /// 用增量而非比例：宿主宽度里含边框与滚动条等固定开销，
        /// 「宿主宽度 ÷ CSS 宽度」会把这段常量摊进系数，测量稍有偏差就整体放大。
        /// 改成「当前宿主宽度 + CSS 差值 × 设备像素比」后常量自然抵消。
        /// </summary>
        /// <param name="hostWidth">当前宿主宽度。</param>
        /// <param name="currentCss">与 <paramref name="hostWidth"/> 对应的 CSS 宽度。</param>
        /// <param name="targetCss">目标 CSS 宽度。</param>
        /// <param name="devicePixelRatio">面板报告的设备像素比。</param>
        /// <param name="maxWidth">宿主宽度上限，非正数表示不限制。</param>
        /// <returns>建议的宿主宽度；无需加宽时返回 <paramref name="hostWidth"/>。</returns>
        internal static int HostWidthForCss(
            int hostWidth,
            int currentCss,
            int targetCss,
            double devicePixelRatio,
            int maxWidth)
        {
            if (hostWidth <= 0 || currentCss <= 0)
            {
                return hostWidth;
            }

            var desired = hostWidth + (int)Math.Ceiling((targetCss - currentCss) * Scale(devicePixelRatio));

            if (maxWidth > 0 && desired > maxWidth)
            {
                desired = maxWidth;
            }

            return desired;
        }

        /// <summary>
        /// 收敛设备像素比。
        /// 缺失或明显不合理时退回 1：宁可少加宽，也不要算出离谱的宽度把工作表挤没。
        /// </summary>
        internal static double Scale(double devicePixelRatio)
        {
            return devicePixelRatio >= 0.5 && devicePixelRatio <= 8 ? devicePixelRatio : 1.0;
        }
    }
}
