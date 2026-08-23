using System.Collections.Generic;

namespace ChatSheet.AddIn.Tools
{
    /// <summary>
    /// 工具执行结果。
    ///
    /// 失败时不抛异常而是返回结构化错误，原因是这个结果要回传给模型：
    /// 模型需要读到可理解的失败原因（例如超出单元格上限、范围地址非法）
    /// 才能自行改小范围重试，而不是让整轮任务中断。
    /// </summary>
    internal sealed class ToolResult
    {
        private ToolResult(bool ok, object data, string error, string errorCode)
        {
            Ok = ok;
            Data = data;
            Error = error;
            ErrorCode = errorCode;
        }

        internal bool Ok { get; }

        internal object Data { get; }

        internal string Error { get; }

        internal string ErrorCode { get; }

        internal static ToolResult Success(object data)
        {
            return new ToolResult(true, data, null, null);
        }

        internal static ToolResult Failure(string errorCode, string error)
        {
            return new ToolResult(false, null, error, errorCode);
        }

        /// <summary>转成回传给模型的载荷。</summary>
        internal object ToPayload()
        {
            if (Ok)
            {
                return Data;
            }

            return new Dictionary<string, object>
            {
                ["error"] = Error,
                ["error_code"] = ErrorCode,
            };
        }
    }
}
