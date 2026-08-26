using System;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 从服务端错误与模型正文里认出「这个模型缺哪项能力」。
    ///
    /// 全是启发式，因为没有任何协议提供「该模型支持什么」的查询接口——
    /// GET /models 只给名字。判断错的代价是多跑一步（换个形态重试），
    /// 因此宁可宽一点也不要漏：漏了就是整轮失败，用户只看到一条原始 400。
    /// </summary>
    internal static class CapabilitySignals
    {
        /// <summary>
        /// 只对客户端错误做能力判定。
        ///
        /// 5xx 是服务端故障，重试同一个请求就可能成功，交给 RetryPolicy；
        /// 把它当成「不支持工具」会让一次网关抖动永久降级掉这个模型。
        /// </summary>
        private static bool IsClientError(ProviderException ex)
        {
            return ex != null &&
                ex.Code != null &&
                ex.Code.StartsWith("HTTP_4", StringComparison.Ordinal);
        }

        private static bool Mentions(string text, params string[] needles)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 错误是否在说「我不支持工具/函数调用」。
        ///
        /// 认字段名（tools、tool_choice、functionDeclarations）比认自然语言可靠：
        /// 各家的措辞五花八门，但字段名来自协议，是固定的。
        /// </summary>
        internal static bool LooksLikeToolUnsupported(ProviderException ex)
        {
            if (!IsClientError(ex))
            {
                return false;
            }

            var message = ex.Message ?? string.Empty;

            // 图片相关的错误绝不能落到这里：两条回退链各自记档，
            // 混淆会让「看不了图」把工具也一并降级掉。
            if (LooksLikeVisionUnsupported(ex))
            {
                return false;
            }

            return Mentions(
                message,
                "tools",
                "tool_choice",
                "tool_calls",
                "function call",
                "function_call",
                "functioncalling",
                "function calling",
                "functiondeclarations",
                "工具调用",
                "不支持函数");
        }

        /// <summary>错误是否在说「我不支持图片输入」。</summary>
        internal static bool LooksLikeVisionUnsupported(ProviderException ex)
        {
            if (!IsClientError(ex))
            {
                return false;
            }

            var message = ex.Message ?? string.Empty;

            return Mentions(
                message,
                "image_url",
                "input_image",
                "inlinedata",
                "image",
                "vision",
                "multimodal",
                "multi-modal",
                "media_type",
                "图片",
                "图像",
                "视觉",
                "多模态");
        }

        /// <summary>
        /// 正文是否在推辞「我碰不到你的表格」。
        ///
        /// 这是不带原生工具能力的模型最常见的表现：服务端收下了工具声明、
        /// 不报任何错，模型却一个调用都不发，只回一句自己做不到
        /// （实测见 DeepSeek-V4-Flash）。此时唯一的信号就是这句话本身。
        ///
        /// 刻意只认「碰不到工作簿」这一类说法，不认泛泛的「我不能」：
        /// 模型拒绝越权请求也会说「我不能」，那是对的，不该触发降级。
        /// </summary>
        internal static bool LooksLikeToolRefusal(string assistantText)
        {
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                return false;
            }

            // 先要出现「做不到」的意思，再要求它谈的是表格。
            // 两者缺一都不算：只提表格是正常作答，只说不能可能是合理拒绝。
            var deniesAbility = Mentions(
                assistantText,
                "无法访问",
                "无法直接访问",
                "不能访问",
                "没有权限",
                "无法读取",
                "无法获取",
                "无法操作",
                "无法直接操作",
                "无法修改",
                "不能修改",
                "看不到",
                "无法查看",
                "没有办法访问",
                "不具备访问",
                "无法连接",
                "cannot access",
                "can't access",
                "unable to access",
                "do not have access",
                "don't have access",
                "no access to",
                "cannot read",
                "unable to read",
                "cannot modify");

            if (!deniesAbility)
            {
                return false;
            }

            return Mentions(
                assistantText,
                "表格",
                "工作簿",
                "工作表",
                "单元格",
                "excel",
                "spreadsheet",
                "workbook",
                "worksheet",
                "cell");
        }
    }
}
