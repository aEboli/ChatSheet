using System;
using System.IO;
using System.Text;

namespace ChatSheet.AddIn.Storage
{
    internal static class AtomicFile
    {
        private static readonly object CommitLock = new object();
        internal static void WriteAllText(string path, string text, string backupPath = null)
        {
            Write(path, stream =>
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 1024, leaveOpen: true))
                {
                    writer.Write(text);
                }
            }, backupPath);
        }

        internal static void WriteAllBytes(string path, byte[] bytes)
        {
            Write(path, stream => stream.Write(bytes, 0, bytes.Length));
        }

        internal static void Write(string path, Action<Stream> write, string backupPath = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }
                lock (CommitLock)
                {
                    if (File.Exists(path)) { File.Replace(temp, path, backupPath); }
                    else
                    {
                        try { File.Move(temp, path); }
                        catch (IOException) when (File.Exists(temp) && File.Exists(path))
                        {
                            // 另一写入者已完成首次创建；仍用完整文件原子替换。
                            File.Replace(temp, path, backupPath);
                        }
                    }
                }
            }
            finally
            {
                try { if (File.Exists(temp)) { File.Delete(temp); } }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
