using System;
using System.IO;
using ChatSheet.AddIn.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatSheet.AddIn.Storage
{
    /// <summary>接入模式。</summary>
    internal enum ConnectionMode
    {
        /// <summary>读取本机 CLI 配置中的接口地址与令牌。</summary>
        LocalCli = 0,

        /// <summary>自定义接口地址、密钥与模型。</summary>
        CustomApi = 1,

        /// <summary>授权登录。占位，暂不实现。</summary>
        Authorized = 2,
    }

    /// <summary>审批策略。</summary>
    internal enum ApprovalPolicy
    {
        /// <summary>写操作逐项审批，读操作自动执行。</summary>
        PerWrite = 0,

        /// <summary>每轮任务开始前统一确认一次。</summary>
        PerTurn = 1,

        /// <summary>全自动执行，依赖撤销兜底。</summary>
        Automatic = 2,
    }

    /// <summary>
    /// 用户设置。密钥不存在这里，只存于 DPAPI 加密的 SecretStore，
    /// 本文件是明文 JSON，任何敏感值都不得写入。
    /// </summary>
    internal sealed class Settings
    {
        internal const string CustomApiSecretKey = "custom-api-token";

        internal ConnectionMode Mode { get; set; } = ConnectionMode.LocalCli;

        internal CliKind CliSource { get; set; } = CliKind.Auto;

        internal ProtocolKind CustomProtocol { get; set; } = Protocols.Default;

        internal string CustomBaseUrl { get; set; } = string.Empty;

        internal string Model { get; set; } = string.Empty;

        /// <summary>
        /// 默认 High：多数模型自身的默认档也是 high，
        /// 且表格任务常涉及多步推理，档位过低会让模型跳过必要的确认。
        /// </summary>
        internal ThinkingLevel Thinking { get; set; } = ThinkingLevel.High;

        internal ApprovalPolicy Approval { get; set; } = ApprovalPolicy.PerWrite;

        internal double? Temperature { get; set; }

        internal int MaxOutputTokens { get; set; } = 8192;

        /// <summary>上下文 token 预算。超出后压缩较早的轮次。</summary>
        internal int ContextBudgetTokens { get; set; } = 100_000;

        /// <summary>Agent 单轮最多允许的工具调用步数，防止失控循环。</summary>
        internal int MaxSteps { get; set; } = 40;

        internal bool AutoIncludeSelection { get; set; } = true;

        /// <summary>
        /// 接入模式变化时，只有前端明确标记为旧覆盖值才清空模型。
        /// 新模式下刚选的模型（即使与旧模型同名）必须保留。
        /// </summary>
        internal void ResetModelIfModeChanged(ConnectionMode previousMode, bool clearRequested)
        {
            if (Mode != previousMode && clearRequested)
            {
                Model = string.Empty;
            }
        }

        /// <summary>
        /// 侧边栏宽度，单位是宿主的窗格单位（随显示缩放变化，并非 CSS 像素）。
        /// 0 表示尚未记录，此时由面板自行校准一次。
        ///
        /// 必须持久化：不记住的话每次打开都要重新按当前视口反推宽度，
        /// 而反推依赖一次瞬时测量，结果每次都不同，表现为面板宽度自己跳动。
        /// </summary>
        internal int PaneWidth { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatSheet",
            "settings.json");

        internal static Settings Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path))
                {
                    return new Settings();
                }

                var root = JObject.Parse(File.ReadAllText(path));
                var settings = new Settings
                {
                    CustomBaseUrl = root.Value<string>("customBaseUrl") ?? string.Empty,
                    Model = root.Value<string>("model") ?? string.Empty,
                    Temperature = root.Value<double?>("temperature"),
                    MaxOutputTokens = root.Value<int?>("maxOutputTokens") ?? 8192,
                    ContextBudgetTokens = root.Value<int?>("contextBudgetTokens") ?? 100_000,
                    MaxSteps = root.Value<int?>("maxSteps") ?? 40,
                    AutoIncludeSelection = root.Value<bool?>("autoIncludeSelection") ?? true,
                    PaneWidth = root.Value<int?>("paneWidth") ?? 0,
                };

                if (Enum.TryParse(root.Value<string>("mode"), out ConnectionMode mode)) { settings.Mode = mode; }
                if (Enum.TryParse(root.Value<string>("cliSource"), out CliKind cli)) { settings.CliSource = cli; }
                if (Protocols.TryParse(root.Value<string>("customProtocol"), out var protocol)) { settings.CustomProtocol = protocol; }
                // 必须用完整限定名：本类的 Thinking 属性会遮蔽 Providers.Thinking 静态类。
                if (Providers.Thinking.TryParse(root.Value<string>("thinking"), out var thinking))
                {
                    settings.Thinking = thinking;
                }
                if (Enum.TryParse(root.Value<string>("approval"), out ApprovalPolicy approval)) { settings.Approval = approval; }

                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                // 设置损坏不应阻塞使用，退回默认值并保留原文件供排查。
                Log.Error("读取设置失败，已回退默认值", ex);
                return new Settings();
            }
        }

        internal void Save()
        {
            try
            {
                Normalize();

                var root = new JObject
                {
                    ["mode"] = Mode.ToString(),
                    ["cliSource"] = CliSource.ToString(),
                    ["customProtocol"] = Protocols.Get(CustomProtocol).Id,
                    ["customBaseUrl"] = CustomBaseUrl ?? string.Empty,
                    ["model"] = Model ?? string.Empty,
                    ["thinking"] = Thinking.ToString(),
                    ["approval"] = Approval.ToString(),
                    ["maxOutputTokens"] = MaxOutputTokens,
                    ["contextBudgetTokens"] = ContextBudgetTokens,
                    ["maxSteps"] = MaxSteps,
                    ["autoIncludeSelection"] = AutoIncludeSelection,
                    ["paneWidth"] = PaneWidth,
                };

                if (Temperature.HasValue)
                {
                    root["temperature"] = Temperature.Value;
                }

                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // 先写临时文件再替换：中途崩溃不会留下半截 JSON。
                var temp = path + ".tmp";
                File.WriteAllText(temp, root.ToString(Formatting.Indented), new System.Text.UTF8Encoding(true));
                if (File.Exists(path)) { File.Delete(path); }
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log.Error("保存设置失败", ex);
                throw new ProviderException("SETTINGS_SAVE_FAILED", "保存设置失败：" + ex.Message, ex);
            }
        }

        /// <summary>把越界值收敛到合理区间，避免用户或损坏文件导致异常行为。</summary>
        private void Normalize()
        {
            if (MaxOutputTokens < 256) { MaxOutputTokens = 256; }
            if (MaxOutputTokens > 200_000) { MaxOutputTokens = 200_000; }

            if (ContextBudgetTokens < 8_000) { ContextBudgetTokens = 8_000; }
            if (ContextBudgetTokens > 2_000_000) { ContextBudgetTokens = 2_000_000; }

            if (MaxSteps < 1) { MaxSteps = 1; }
            if (MaxSteps > 200) { MaxSteps = 200; }

            // 0 保留「未记录」语义；其余值收敛到可用区间，
            // 损坏的极端值会让面板窄到无法操作或宽到盖住工作表。
            if (PaneWidth != 0)
            {
                if (PaneWidth < 200) { PaneWidth = 200; }
                if (PaneWidth > 4000) { PaneWidth = 4000; }
            }

            if (Temperature.HasValue)
            {
                if (Temperature.Value < 0) { Temperature = 0; }
                if (Temperature.Value > 2) { Temperature = 2; }
            }
        }

        /// <summary>
        /// 解析出可用于发起请求的接入信息。
        /// 令牌只在返回值中短暂存在，不写日志、不回传面板。
        /// </summary>
        internal ResolvedConnection ResolveConnection()
        {
            switch (Mode)
            {
                case ConnectionMode.CustomApi:
                {
                    if (string.IsNullOrWhiteSpace(CustomBaseUrl))
                    {
                        throw new ProviderException("BASE_URL_REQUIRED", "尚未填写接口地址。");
                    }

                    var token = SecretStore.Load(CustomApiSecretKey);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new ProviderException("TOKEN_REQUIRED", "尚未填写接口密钥。");
                    }

                    return new ResolvedConnection
                    {
                        Protocol = CustomProtocol,
                        BaseUrl = Protocols.NormalizeBaseUrl(CustomBaseUrl, CustomProtocol),
                        Token = token,
                        Model = Model,
                        SourceLabel = "自定义接口",
                    };
                }

                case ConnectionMode.Authorized:
                    throw new ProviderException("MODE_NOT_IMPLEMENTED", "授权登录模式尚未实现，请改用本地 CLI 配置或自定义接口。");

                default:
                {
                    var credentials = LocalCliConfig.Resolve(CliSource);
                    return new ResolvedConnection
                    {
                        Protocol = credentials.Protocol,
                        BaseUrl = credentials.BaseUrl,
                        Token = credentials.Token,
                        // 用户未指定模型时沿用 CLI 配置中的模型。
                        Model = string.IsNullOrWhiteSpace(Model) ? credentials.Model : Model,
                        SourceLabel = credentials.DisplayName,
                    };
                }
            }
        }
    }

    internal sealed class ResolvedConnection
    {
        internal ProtocolKind Protocol { get; set; }

        internal string BaseUrl { get; set; }

        internal string Token { get; set; }

        internal string Model { get; set; }

        internal string SourceLabel { get; set; }
    }
}
