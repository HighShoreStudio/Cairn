using System.Text;
using System.Text.Json;

namespace HighshoreCairn.Services;

/// <summary>Encrypts/decrypts a blob for the current user of this PC.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] encrypted);
}

/// <summary>
/// Reads and writes an encrypted JSON file. On disk the file is a small JSON envelope:
/// { "format": "HighshoreKanban.Protected", "version": 1, "data": "base64..." }
/// </summary>
public class ProtectedFile
{
    private const string Format = "HighshoreKanban.Protected";
    private readonly IDataProtector _protector;

    public ProtectedFile(IDataProtector protector) => _protector = protector;

    private class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; } = 1;
        public string Data { get; set; } = "";
    }

    public T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        var envelope = JsonFile.Deserialize<Envelope>(File.ReadAllText(path, Encoding.UTF8));
        if (envelope is null || envelope.Format != Format)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a valid protected file.");
        var plain = _protector.Unprotect(Convert.FromBase64String(envelope.Data));
        return JsonSerializer.Deserialize<T>(plain, JsonFile.Options);
    }

    public void Save<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonFile.Options);
        var envelope = new Envelope
        {
            Format = Format,
            Data = Convert.ToBase64String(_protector.Protect(plain))
        };
        JsonFile.Save(path, envelope);
    }
}
