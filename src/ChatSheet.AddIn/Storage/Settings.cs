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
        /// <see cref="Model"/> 是为哪个接入连接选的（见 <see cref="ConnectionKey"/>）。
        ///
        /// 必须持久化：模型只对选它的那个连接有意义。没有这个标记时，
        /// 从「自定义接口」切回「本机 CLI 配置」后，旧模型会继续留在 Model 里，
        /// 而 <see cref="ResolveConnection"/> 又优先用 Model，于是 CLI 配置自带的模型
        /// 被一个它根本没有的模型名顶掉，界面上还显示为「已就绪」。
        ///
        /// 空串表示来历不明（旧版设置文件），由 <see cref="AdoptOrDropUnstampedModel"/> 决定去留。
        /// </summary>
        internal string ModelConnection { get; set; } = string.Empty;

        /// <summary>
        /// 默认 High：多数模型自身的默认档也是 high，
        /// 且表格任务常涉及多步推理，档位过低会让模型跳过必要的确认。
        /// </summary>
        internal ThinkingLevel Thinking { get; set; } = ThinkingLevel.High;

        internal ApprovalPolicy Approval { get; set; } = ApprovalPolicy.PerWrite;

        internal double? Temperature { get; set; }

        internal int MaxOutputTokens { get; set; } = 8192;

        /// <summary>
        /// 上下文 token 预算。超出后压缩较早的轮次。
        ///
        /// 取 200000 而非窗口全额 272000：估算器不计入图片（只累加文本与工具调用），
        /// 输出 token 在多数服务商与输入共享同一窗口，且启发式估算在纯数字上可能偏低。
        /// 留出的余量正是给这三项。
        /// </summary>
        internal int ContextBudgetTokens { get; set; } = 200_000;

        /// <summary>Agent 单轮最多允许的工具调用步数，防止失控循环。</summary>
        internal int MaxSteps { get; set; } = 40;

        internal bool AutoIncludeSelection { get; set; } = true;

        /// <summary>
        /// 当前接入连接的稳定标识。
        ///
        /// 只包含会改变「有哪些模型可用」的字段：自定义接口看协议与地址，
        /// 本机 CLI 看用的是哪个 CLI。密钥绝不进入该键，它会随设置一起明文落盘。
        /// </summary>
        internal string ConnectionKey()
        {
            if (Mode != ConnectionMode.CustomApi)
            {
                return Mode + "|" + CliSource;
            }

            // 地址先规范化，避免尾斜杠、缺少 /v1 这类等价写法被判成换了连接。
            var address = (CustomBaseUrl ?? string.Empty).Trim();
            try
            {
                address = Protocols.NormalizeBaseUrl(address, CustomProtocol);
            }
            catch (ProviderException)
            {
                // 地址还没填完或填错时用原样文本，此时本就不该复用别处的模型。
            }

            return Mode + "|" + Protocols.Get(CustomProtocol).Id + "|" + address;
        }

        /// <summary>把当前模型登记为「为当前连接所选」。用户主动选定模型后调用。</summary>
        internal void StampModelConnection()
        {
            ModelConnection = string.IsNullOrWhiteSpace(Model) ? string.Empty : ConnectionKey();
        }

        /// <summary>
        /// 丢弃不属于当前连接的模型。
        ///
        /// 这是「切回本机 CLI 配置后仍在用自定义接口的模型」的根治点：
        /// 无论这个值是怎么留下来的（保存竞态、面板状态过期、旧版设置文件），
        /// 只要它的登记连接和当前连接不一致就不再生效。
        /// </summary>
        internal bool DropModelFromOtherConnection()
        {
            if (string.IsNullOrWhiteSpace(Model) ||
                string.IsNullOrEmpty(ModelConnection) ||
                ModelConnection == ConnectionKey())
            {
                return false;
            }

            Model = string.Empty;
            ModelConnection = string.Empty;
            return true;
        }

        /// <summary>
        /// 保存时决定是否留用传入的模型。
        ///
        /// <paramref name="chosenForConnection"/> 由面板给出，表示这个模型是用户在
        /// 当前这套接入配置下选的。没有这个确认而连接又变了，模型只能是上一套配置的
        /// 残留——面板的表单状态可能比磁盘旧，所以不能只看模式有没有变。
        /// </summary>
        internal void KeepModelOnlyIfChosenForConnection(string previousConnectionKey, bool chosenForConnection)
        {
            if (chosenForConnection || ConnectionKey() == previousConnectionKey)
            {
                StampModelConnection();
                return;
            }

            Model = string.Empty;
            ModelConnection = string.Empty;
        }

        /// <summary>
        /// 处理旧版设置文件里没有登记连接的模型。
        ///
        /// 自定义接口的模型只可能是为它自己选的，直接认领；本机 CLI 则无从判断，
        /// 而这正是缺陷的高发处，因此丢弃并回落到 CLI 配置自带的模型——
        /// 代价是升级后可能需要重选一次，换来的是不会继续用一个错的模型名发请求。
        /// </summary>
        internal void AdoptOrDropUnstampedModel()
        {
            if (string.IsNullOrWhiteSpace(Model))
            {
                Model = string.Empty;
                ModelConnection = string.Empty;
                return;
            }

            if (Mode == ConnectionMode.CustomApi)
            {
                ModelConnection = ConnectionKey();
                return;
            }

            Model = string.Empty;
            ModelConnection = string.Empty;
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
                    ModelConnection = root.Value<string>("modelConnection") ?? string.Empty,
                    Temperature = root.Value<double?>("temperature"),
                    MaxOutputTokens = root.Value<int?>("maxOutputTokens") ?? 8192,
                    ContextBudgetTokens = root.Value<int?>("contextBudgetTokens") ?? 200_000,
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

                // 模式等字段都读完才能判断模型归属，因此收敛放在最后。
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
                    ["modelConnection"] = ModelConnection ?? string.Empty,
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
            // 模型归属先收敛：读盘与保存都会经过这里，是唯一能保证
            // 「内存里的 Model 一定属于当前连接」的地方。
            if (string.IsNullOrEmpty(ModelConnection))
            {
                AdoptOrDropUnstampedModel();
            }
            else
            {
                DropModelFromOtherConnection();
            }

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
