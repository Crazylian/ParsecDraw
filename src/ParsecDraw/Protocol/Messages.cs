using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParsecDraw.Protocol;

public enum ToolKind : byte
{
    Pen = 0,
    Laser = 1,
    Eraser = 2,
}

/// <summary>
/// 统一的扁平消息结构。t 决定语义，其余字段按消息类型取用，减少 schema 膨胀。
/// 坐标一律归一化到 [0,1]（相对主机屏幕），各端按自身窗口尺寸映射。
/// </summary>
public sealed record Msg
{
    /// <summary>消息类型，见 <see cref="MsgTypes"/>。</summary>
    [JsonPropertyName("t")] public string T { get; init; } = "";

    /// <summary>笔迹 id（clientId-序号），begin/point/end/undo 时有效。</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }

    /// <summary>发送者昵称（begin/join 时有效）。</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>笔颜色 #RRGGBB（begin 时有效）。</summary>
    [JsonPropertyName("c")] public string? Color { get; init; }

    /// <summary>基准粗细（px @1080p，begin 时有效）；host 消息里表示主机屏幕宽（物理 px）。</summary>
    [JsonPropertyName("w")] public double W { get; init; }

    /// <summary>主机屏幕高（物理 px，仅 host 消息；其余消息为 null 不上线）。</summary>
    [JsonPropertyName("h")] public double? H { get; init; }

    /// <summary>工具类型（begin 时有效）。</summary>
    [JsonPropertyName("k")] public ToolKind K { get; init; } = ToolKind.Pen;

    /// <summary>归一化坐标批次 [[x,y],...]（begin/point 时有效）。</summary>
    [JsonPropertyName("p")] public double[][]? P { get; init; }

    /// <summary>end 时是否直接钉住。</summary>
    [JsonPropertyName("pin")] public bool Pin { get; init; }
}

public static class MsgTypes
{
    public const string Join = "join";
    public const string Begin = "begin";
    public const string Point = "point";
    public const string End = "end";
    public const string Undo = "undo";
    public const string Clear = "clear";
    public const string Pin = "pin";

    /// <summary>主机通告屏幕分辨率（服务器单向发给观众，观众端做黑边/对齐补偿）。</summary>
    public const string Host = "host";
}

public sealed record Settings
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "观众";
    [JsonPropertyName("color")] public string Color { get; init; } = "#FF4757";
    [JsonPropertyName("width")] public double Width { get; init; } = 6;
    [JsonPropertyName("showAll")] public bool ShowAll { get; init; } = false;
}

public static class SettingsStore
{
    public static string FilePath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ParsecDraw",
            "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.Settings) ?? new Settings();
        }
        catch
        {
            // 设置损坏时静默回落到默认值
        }
        return new Settings();
    }

    public static void Save(Settings s)
    {
        try
        {
            // 建目录只在写路径做（读路径不应有副作用）
            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, AppJsonContext.Default.Settings));
        }
        catch { /* 保存失败不影响使用 */ }
    }
}

/// <summary>
/// System.Text.Json source-gen 上下文：编译期生成序列化代码，零反射开销。
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Msg))]
[JsonSerializable(typeof(Settings))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
