using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveSync.Core;

/// <summary>Shared JSON settings. Stable on-disk shape: camelCase, indented, tolerant of unknown fields.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? ReadFile<T>(string path)
    {
        if (!File.Exists(path)) return default;
        for (int attempt = 0; ; attempt++)
        {
            try { return Read<T>(File.ReadAllText(path)); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(60); }
            catch (JsonException) { return default; }
            catch (UnauthorizedAccessException) { return default; }
        }
    }

    /// <summary>Write via temp then replace, so a crash never leaves a truncated file behind.</summary>
    public static void WriteFileAtomic<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Write(value));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}
