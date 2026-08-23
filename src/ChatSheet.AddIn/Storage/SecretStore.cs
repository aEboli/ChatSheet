using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChatSheet.AddIn.Storage
{
    /// <summary>
    /// 密钥存储。使用 Windows DPAPI 以当前用户范围加密，
    /// 密文只能由同一 Windows 用户在同一台机器上解开。
    ///
    /// 关键约束：密钥永不回传给面板 UI。面板只能得到「是否已配置」
    /// 和末四位掩码，实际请求全部由加载项侧发起。
    /// 这样即使面板页面被注入脚本，也拿不到凭据。
    /// </summary>
    internal static class SecretStore
    {
        // 附加熵：即使密文被复制到同一用户的其他程序，也无法在缺少该熵时解开。
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChatSheet.SecretStore.v1");

        private static string StoreDirectory
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChatSheet",
                    "secrets");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string PathFor(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("密钥标识不能为空。", nameof(key));
            }

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                key = key.Replace(invalid, '_');
            }

            return Path.Combine(StoreDirectory, key + ".bin");
        }

        internal static void Save(string key, string secret)
        {
            var path = PathFor(key);

            if (string.IsNullOrEmpty(secret))
            {
                Delete(key);
                return;
            }

            var plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(path, cipher);
            }
            finally
            {
                // 及时清除明文副本，缩短其在内存中的存活时间。
                Array.Clear(plain, 0, plain.Length);
            }
        }

        internal static string Load(string key)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var cipher = File.ReadAllBytes(path);
                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            catch (CryptographicException ex)
            {
                // 换机器或换用户后密文无法解开，这是预期情况：
                // 视为「未配置」，让用户重新填写，而不是抛错阻塞。
                Log.Warn($"密钥 {key} 无法解密（通常因更换用户或机器）：{ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"读取密钥 {key} 失败", ex);
                return null;
            }
        }

        internal static bool Exists(string key)
        {
            return File.Exists(PathFor(key));
        }

        internal static void Delete(string key)
        {
            try
            {
                var path = PathFor(key);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"删除密钥 {key} 失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 生成用于界面展示的掩码，例如 sk-…a1b2。
        /// 只暴露末四位，既能让用户确认填的是哪一个，又不泄露密钥。
        /// </summary>
        internal static string Mask(string secret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                return string.Empty;
            }

            var tail = secret.Length <= 4 ? secret : secret.Substring(secret.Length - 4);
            return "…" + tail;
        }
    }
}
