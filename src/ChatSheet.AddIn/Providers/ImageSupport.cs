using System;
using System.Collections.Generic;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 一张随消息发送的图片。
    ///
    /// 只保留 base64 与媒体类型：面板通过消息桥传来的就是 data URL，
    /// 加载项侧不落盘也不上传到任何第三方存储。
    /// </summary>
    internal sealed class ImageAttachment
    {
        internal string MediaType { get; set; }

        /// <summary>不含 data URL 前缀的纯 base64 数据。</summary>
        internal string Base64 { get; set; }

        /// <summary>原始文件名，仅用于日志与界面展示。</summary>
        internal string Name { get; set; }

        internal int ByteLength { get; set; }

        /// <summary>拼回 data URL，OpenAI 系协议需要这种形式。</summary>
        internal string ToDataUrl()
        {
            return $"data:{MediaType};base64,{Base64}";
        }
    }

    internal static class ImageSupport
    {
        /// <summary>
        /// 各协议共同支持的格式。
        ///
        /// 取交集而非并集：GIF 在 OpenAI 与 Anthropic 可用但 Gemini 不支持，
        /// heic/heif 只有 Gemini 支持。让面板只接受三方通吃的三种，
        /// 用户就不会遇到「换个模型同一张图就报错」。
        /// </summary>
        internal static readonly string[] SupportedMediaTypes =
        {
            "image/png",
            "image/jpeg",
            "image/webp",
        };

        /// <summary>
        /// 单张图片的字节上限。
        ///
        /// 取 5MB 是三方限制里最保守的那档（Anthropic 经 Bedrock/Vertex 时为 5MB），
        /// 直连 Claude API 允许 10MB。按最严的来，换服务商不会突然失败。
        /// </summary>
        internal const int MaxBytesPerImage = 5 * 1024 * 1024;

        /// <summary>单轮最多附带的图片数。窄栏面板里更多也难以查看。</summary>
        internal const int MaxImagesPerTurn = 6;

        internal static bool IsSupported(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return false;
            }

            foreach (var type in SupportedMediaTypes)
            {
                if (string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 解析 data URL。
        /// 面板传来的形如 data:image/png;base64,iVBORw0...
        /// </summary>
        internal static ImageAttachment ParseDataUrl(string dataUrl, string name)
        {
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                throw new ProviderException("IMAGE_EMPTY", "图片数据为空。");
            }

            const string prefix = "data:";
            const string marker = ";base64,";

            if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderException("IMAGE_INVALID", "图片数据不是合法的 data URL。");
            }

            var markerIndex = dataUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                throw new ProviderException("IMAGE_INVALID", "图片数据缺少 base64 标记。");
            }

            var mediaType = dataUrl.Substring(prefix.Length, markerIndex - prefix.Length).Trim();
            var payload = dataUrl.Substring(markerIndex + marker.Length);

            if (!IsSupported(mediaType))
            {
                throw new ProviderException(
                    "IMAGE_UNSUPPORTED",
                    $"不支持的图片格式 {mediaType}。请使用 PNG、JPEG 或 WebP。");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                throw new ProviderException("IMAGE_INVALID", "图片的 base64 数据无法解码。");
            }

            if (bytes.Length > MaxBytesPerImage)
            {
                throw new ProviderException(
                    "IMAGE_TOO_LARGE",
                    $"图片 {name} 为 {bytes.Length / 1024 / 1024.0:F1} MB，超过 5 MB 上限。请压缩后再试。");
            }

            return new ImageAttachment
            {
                MediaType = mediaType,
                Base64 = payload,
                Name = name,
                ByteLength = bytes.Length,
            };
        }

        /// <summary>
        /// 该协议是否支持图片输入。
        /// 四种协议都支持，但具体模型可能不支持——那只能等服务端报错，
        /// 请求前无法可靠判断，因此这里只按协议放行。
        /// </summary>
        internal static bool ProtocolSupportsImages(ProtocolKind protocol)
        {
            return true;
        }

        internal static string Describe(IReadOnlyList<ImageAttachment> images)
        {
            if (images == null || images.Count == 0)
            {
                return "无图片";
            }

            var total = 0;
            foreach (var image in images)
            {
                total += image.ByteLength;
            }

            return $"{images.Count} 张图片，合计 {total / 1024.0:F0} KB";
        }
    }
}
