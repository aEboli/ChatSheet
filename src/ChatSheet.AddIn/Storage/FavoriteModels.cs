using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Storage
{
    /// <summary>
    /// 用户「常用哪几个模型」的名单，按连接分组落盘。
    ///
    /// 这是用户意图，不是探测结论，所以落盘——与可用性判定（只在内存里）恰好相反。
    ///
    /// 为什么另存一个文件而不进 settings.json：后者的 Save 按白名单整体重建文档，
    /// 任何只读不写的键会被下一次任意写入方抹掉（面板宽度、主题都会触发），
    /// 而名单的归属是连接、不是设置。
    /// </summary>
    internal static class FavoriteModels
    {
        /// <summary>
        /// 名单文件。放 %LOCALAPPDATA%\ChatSheet\ 下，与 settings.json 同级。
        ///
        /// 刻意不进 SecretStore 的 secrets 子目录：那里装的是 DPAPI 密文，
        /// 而本文件是明文 JSON，混在一起会让「这个目录里的东西都是密文」不再成立。
        ///
        /// 路径可注入是为了可测：Settings.FilePath 是 private static，
        /// 于是设置的读写在测试里零覆盖——名单丢了是用户成果没了，不能也这样。
        /// 写法照 LocalCliConfig.ClaudeSettingsPath。
        /// </summary>
        internal static string FilePathFor(string rootDir = null)
        {
            var dir = rootDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatSheet");

            return Path.Combine(dir, "favorite-models.json");
        }

        /// <summary>
        /// 读出一个连接的名单。读不到、解析失败、该连接没有分组都返回空名单。
        ///
        /// 只碰当前连接那一组：分组文件里「归属不是我」是其余每一组的常态，
        /// 不是要清理的错误状态。刻意不照搬 Settings.DropModelFromOtherConnection
        /// 的处置——那是为「只存一个模型」写的，照搬会在读盘时删掉其他所有连接的分组。
        /// </summary>
        internal static IReadOnlyList<string> Load(string connectionKey, string rootDir = null)
        {
            return WithLock(() => LoadCore(connectionKey, rootDir));
        }

        private static IReadOnlyList<string> LoadCore(string connectionKey, string rootDir)
        {
            var groups = ReadGroups(rootDir);
            return groups.TryGetValue(connectionKey ?? string.Empty, out var models)
                ? models
                : new List<string>();
        }

        /// <summary>
        /// 写回一个连接的名单，其余连接的分组按原样保留。
        /// </summary>
        internal static void Save(string connectionKey, IEnumerable<string> models, string rootDir = null)
        {
            WithLock(() => { SaveCore(connectionKey, models, rootDir); return true; });
        }

        private static void SaveCore(string connectionKey, IEnumerable<string> models, string rootDir)
        {
            var key = connectionKey ?? string.Empty;
            var groups = ReadGroups(rootDir, requireValid: true);
            var kept = Normalize(models);

            if (kept.Count == 0)
            {
                groups.Remove(key);
            }
            else
            {
                groups[key] = kept;
            }

            WriteGroups(groups, rootDir);
        }

        /// <summary>加入或移出名单，返回操作后是否在名单里。</summary>
        internal static bool Toggle(string connectionKey, string model, string rootDir = null)
        {
            return WithLock(() => ToggleCore(connectionKey, model, rootDir));
        }

        private static bool ToggleCore(string connectionKey, string model, string rootDir)
        {
            var id = (model ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                return false;
            }

            var current = Load(connectionKey, rootDir).ToList();
            var index = current.FindIndex(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                current.RemoveAt(index);
                Save(connectionKey, current, rootDir);
                return false;
            }

            current.Add(id);
            Save(connectionKey, current, rootDir);
            return true;
        }

        /// <summary>把一个模型并入名单；已在名单里则什么都不做。</summary>
        internal static void Add(string connectionKey, string model, string rootDir = null)
        {
            WithLock(() => { AddCore(connectionKey, model, rootDir); return true; });
        }

        private static void AddCore(string connectionKey, string model, string rootDir)
        {
            var id = (model ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                return;
            }

            var current = Load(connectionKey, rootDir).ToList();
            if (current.Any(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            current.Add(id);
            Save(connectionKey, current, rootDir);
        }

        /// <summary>
        /// 去重并去空。比较忽略大小写，与 ChatClient.ExtractModelIds 的去重同口径：
        /// 否则 GPT-4O 与 gpt-4o 会在名单里各占一行，筛选时也对不上目录。
        /// </summary>
        private static List<string> Normalize(IEnumerable<string> models)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<string>();

            foreach (var model in models ?? Enumerable.Empty<string>())
            {
                var id = (model ?? string.Empty).Trim();
                if (id.Length > 0 && seen.Add(id))
                {
                    kept.Add(id);
                }
            }

            return kept;
        }

        private static T WithLock<T>(Func<T> work)
        {
            // 覆盖同一 Windows 会话内的 Excel、WPS 和多个面板，锁住完整读改写。
            using (var mutex = new Mutex(false, @"Local\ChatSheet.FavoriteModels"))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { throw new ProviderException("FAVORITES_BUSY", "常用模型正在保存，请稍后重试。"); }
                    return work();
                }
                finally { if (acquired) { mutex.ReleaseMutex(); } }
            }
        }

        private static Dictionary<string, List<string>> ReadGroups(string rootDir, bool requireValid = false)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            try
            {
                var path = FilePathFor(rootDir);
                if (!File.Exists(path))
                {
                    return groups;
                }

                var root = JObject.Parse(File.ReadAllText(path));
                var connections = root["connections"] as JObject;
                if (connections == null)
                {
                    if (requireValid) { throw new JsonException("缺少模型收藏分组"); }
                    return groups;
                }

                foreach (var pair in connections)
                {
                    if (pair.Value is JArray list)
                    {
                        groups[pair.Key] = Normalize(list.Select(v => v.Value<string>()));
                    }
                }
            }
            catch (Exception ex)
            {
                // 损坏不该阻塞使用：退回空名单并保留原文件供排查。
                // 与 Settings.Load 的处置一致，但后果更重——那边退回的是可再生的默认值，
                // 这边退回的是「看起来一个都没标过」，所以绝不能顺手把文件删掉或覆盖。
                Log.Warn("读取常用模型名单失败，本次按空名单处理：" + ex.Message);
                if (requireValid) { throw new ProviderException("FAVORITES_READ_FAILED", "常用模型文件无法读取，原文件已保留，请检查后重试。", ex); }
                return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            }

            return groups;
        }

        private static void WriteGroups(Dictionary<string, List<string>> groups, string rootDir)
        {
            try
            {
                var connections = new JObject();
                foreach (var pair in groups)
                {
                    connections[pair.Key] = new JArray(pair.Value.Cast<object>().ToArray());
                }

                var root = new JObject { ["connections"] = connections };

                var path = FilePathFor(rootDir);
                AtomicFile.WriteAllText(path, root.ToString(Formatting.Indented), path + ".bak");
            }
            catch (Exception ex)
            {
                Log.Error("保存常用模型名单失败", ex);
                throw new ProviderException(
                    "FAVORITES_SAVE_FAILED", "保存常用模型名单失败：" + ex.Message, ex);
            }
        }
    }
}
