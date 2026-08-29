using System;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>
    /// 从服务端错误与模型正文里认出「这个模型缺哪项能力」。
    ///
    /// 全是启发式，因为没有任何协议提供「该模型支持什么」的查询接口——
    /// GET /models 只给名字。判断错的代价是多跑一步（换个形态重试），
    /// 因此宁可宽一点也不要漏：漏了就是整轮失败，用户只看到一条原始 400。
    ///
    /// IsClientError 与 Mentions 是 internal，供 ModelAvailability 复用——
    /// 可用性判定与这里同源同风格，各写一份迟早会分叉。
    /// </summary>
    internal static class CapabilitySignals
    {
        /// <summary>
        /// 只对客户端错误做能力判定。
        ///
        /// 5xx 是服务端故障，重试同一个请求就可能成功，交给 RetryPolicy；
        /// 把它当成「不支持工具」会让一次网关抖动永久降级掉这个模型。
        /// </summary>
        internal static bool IsClientError(ProviderException ex)
        {
            return ex != null &&
                ex.Code != null &&
                ex.Code.StartsWith("HTTP_4", StringComparison.Ordinal);
        }

        internal static bool Mentions(string text, params string[] needles)
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

            // 错误在说「问题出在模型本身」时，它不是任何能力信号。
            if (BlamesModelItself(ex))
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

        /// <summary>
        /// 错误是否在说「我不支持图片输入」。
        ///
        /// 本判据认裸子串 image，而模型名本身就可能含 image
        /// （gpt-image-1、*-image-preview），那样一条 404 会被读成「不支持图片」。
        /// 排除靠 BlamesModelItself，详见那里。
        /// </summary>
        internal static bool LooksLikeVisionUnsupported(ProviderException ex)
        {
            if (!IsClientError(ex))
            {
                return false;
            }

            if (BlamesModelItself(ex))
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
        /// 错误是否在说「问题出在这个模型本身」，而不是在说缺哪项能力。
        ///
        /// 这是两条能力判据共同的前置排除。没有它就有一个真实缺陷：
        /// LooksLikeVisionUnsupported 认裸子串 "image"，于是选 gpt-image-1、附一张图、
        /// 发送时，那句 `model 'gpt-image-1' does not exist` 是 4xx 且含 image，
        /// 会被记成「不支持图片输入」——白花一次视觉中转请求去描述图片、剥掉所有图、
        /// 再用同一个不存在的模型重试一遍，最后告诉用户「当前模型没有视觉能力」。
        /// 一条错误产生两个记录，其中一个是假的，而假的那个才是用户看到的。
        ///
        /// 判定委托给 ModelAvailability：那边只读 Detail（服务端原文），
        /// 不读拼过 hint 的 Message，避免我们自己的「请检查……模型名……」变成证据。
        /// </summary>
        private static bool BlamesModelItself(ProviderException ex)
        {
            return ModelAvailability.BlamesModel(ex);
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
