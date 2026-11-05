using System.Text.Json;

namespace FastEncoder.Core;

/// <summary>Persisted user preferences (%LOCALAPPDATA%\FastEncoder\settings.json).</summary>
public sealed class AppSettings
{
    public string PresetId { get; set; } = "balanced";
    public EncodeSettings Encode { get; set; } = new();
    public EditSettings Edit { get; set; } = new();
    public bool SaveNextToSource { get; set; } = true;
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FastEncoder");
    public string Suffix { get; set; } = "_small";
    public bool DeleteBiggerResults { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastEncoder", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
