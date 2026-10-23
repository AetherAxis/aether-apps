using System.Text.Json;
using System.Text.Json.Serialization;

// —— console 配置文件（global namespace：与桥接类同理，避开 monorepo 旧 RootNamespace `Acme.Ctp.Console`
//    的 `Console` 段与 System.Console 同名歧义；本仓 RootNamespace 为 `Aether.CtpConsole`）——

/// <summary>
/// 配置文件（cwd 的 <c>config.json</c>——「一个目录一个实例」，可用 <c>--config</c> 覆盖路径）：前置地址/凭据存档案，
/// 免每次敲长参数串。命令行参数 &gt; 配置档案 &gt; 内置默认。密码明文入档——文件创建时 chmod 600（仅属主可读）。
/// </summary>
public sealed class ConsoleConfig
{
    /// <summary>默认登录档案名（--profile 缺省时用它）。</summary>
    [JsonPropertyName("defaultProfile")]
    public string? DefaultProfile { get; set; }

    /// <summary>labs 目录（缺省与桌面端 Stockapp 共享）。</summary>
    [JsonPropertyName("labs")]
    public string? Labs { get; set; }

    /// <summary>数据目录（ctp.db / lean-algo-cache；缺省 ~/.aether/ctp-console）。</summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>登录档案（名 → 前置/凭据）——一台机器跑多个 console 实例各连一个账户（双 bot 形态）。</summary>
    [JsonPropertyName("profiles")]
    public Dictionary<string, ConsoleProfile> Profiles { get; set; } = new(StringComparer.Ordinal);

    public static ConsoleConfig Load(string path)
    {
        if (!File.Exists(path)) return new ConsoleConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ConsoleConfig>(json, JsonOptions) ?? new ConsoleConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 600：含密码——仅属主可读写（Windows 无此 API 语义，忽略异常）
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        // Windows 没有 Unix 权限位语义，API 直接抛 PlatformNotSupported——先判平台再收紧。
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    /// <summary>生成模板（首次启动无配置时）：带示例档案（占位凭据）+ 全字段说明。</summary>
    public static ConsoleConfig Template() => new()
    {
        DefaultProfile = "示例账户",
        Profiles = new(StringComparer.Ordinal)
        {
            ["示例账户"] = new ConsoleProfile
            {
                Front = "tcp://前置地址:17801",
                MdFront = "tcp://前置地址:17802",
                User = "你的账户",
                Password = "你的密码",
                Broker = "9999",
                AppId = "simnow_client_test",
                AuthCode = "0000000000000000",
            },
        },
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样
    };
}

/// <summary>单个登录档案：CTP 前置 + 凭据（镜像 <see cref="Ctp.Net.CtpLoginRequest"/> 的字段面）。</summary>
public sealed class ConsoleProfile
{
    [JsonPropertyName("front")] public string? Front { get; set; }
    [JsonPropertyName("mdFront")] public string? MdFront { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("broker")] public string? Broker { get; set; }
    [JsonPropertyName("appId")] public string? AppId { get; set; }
    [JsonPropertyName("authCode")] public string? AuthCode { get; set; }
}
