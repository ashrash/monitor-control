using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorSwitcher.Core.Profiles;

namespace MonitorSwitcher.Core.Storage;

/// <summary>Source-generated JSON metadata (required for trimming and Native AOT).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(MonitorProfile))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppState))]
public partial class CoreJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Same metadata, but without HTML-safe escaping, so files people edit by hand read
    /// "Ctrl+Alt+1" rather than "Ctrl+Alt+1". The files are never embedded in HTML.
    /// </summary>
    public static CoreJsonContext Files => s_files ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static CoreJsonContext? s_files;
}
