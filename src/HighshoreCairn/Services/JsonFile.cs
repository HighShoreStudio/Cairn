using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HighshoreCairn.Services;

/// <summary>JSON helpers shared by every storage class (System.Text.Json).</summary>
public static class JsonFile
{
    /// <summary>Indented, camelCase, enums as strings: readable diffs in git.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep copy through a JSON round-trip.</summary>
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value))!;

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        return Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
    }

    public static void Save<T>(string path, T value) => WriteAllTextAtomic(path, Serialize(value) + "\n");

    /// <summary>Writes to a temporary file and then swaps it in, so a crash never leaves a half-written file.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
