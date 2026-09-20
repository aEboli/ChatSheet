using System;
using System.Runtime.InteropServices;

namespace ChatSheet.AddIn.Hosts
{
    /// <summary>保留工作簿实例身份；同名文件或关闭后重开的文件不能继承旧操作。</summary>
    internal sealed class WorkbookTarget : IDisposable
    {
        private object _workbook;
        private bool _disposed;

        private WorkbookTarget(object workbook) { _workbook = workbook; }

        internal static WorkbookTarget Capture(object application)
        {
            return new WorkbookTarget(application == null ? null : Com.Get(application, "ActiveWorkbook"));
        }

        internal bool IsCurrent(object application)
        {
            if (_disposed) { return false; }
            object current = null;
            IntPtr expectedIdentity = IntPtr.Zero, currentIdentity = IntPtr.Zero;
            try
            {
                current = application == null ? null : Com.Get(application, "ActiveWorkbook");
                if (ReferenceEquals(_workbook, current)) { return true; }
                if (_workbook == null || current == null || !Marshal.IsComObject(_workbook) || !Marshal.IsComObject(current)) { return false; }
                expectedIdentity = Marshal.GetIUnknownForObject(_workbook);
                currentIdentity = Marshal.GetIUnknownForObject(current);
                return expectedIdentity == currentIdentity;
            }
            finally
            {
                if (expectedIdentity != IntPtr.Zero) { Marshal.Release(expectedIdentity); }
                if (currentIdentity != IntPtr.Zero) { Marshal.Release(currentIdentity); }
                Com.Release(current);
            }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Com.Release(_workbook);
            _workbook = null;
        }
    }
}
