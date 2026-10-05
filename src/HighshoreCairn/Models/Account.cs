using System.Text.Json.Serialization;

namespace HighshoreCairn.Models;

/// <summary>
/// A local user account. The same shape is used in the global archive
/// (%APPDATA%/Cairn/accounts.json) and in each project's accounts.json
/// (where it is a clone without session tokens).
/// The password is never stored: only a PBKDF2 hash + salt.
/// </summary>
public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = "";
    public string? Email { get; set; }
    public string AvatarColor { get; set; } = "#1F6F8B";

    /// <summary>Optional profile picture: a small square PNG stored as base64 (so it travels with the project).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarImage { get; set; }

    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int Iterations { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>SHA-256 hashes of the valid auto-login tokens. Global archive only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionTokenHashes { get; set; }

    [JsonIgnore]
    public string Initials => MakeInitials(Username);

    /// <summary>Copy stored inside a project folder: no session tokens.</summary>
    public Account CloneForProject() => new()
    {
        Id = Id,
        Username = Username,
        Email = Email,
        AvatarColor = AvatarColor,
        AvatarImage = AvatarImage,
        PasswordHash = PasswordHash,
        PasswordSalt = PasswordSalt,
        Iterations = Iterations,
        CreatedAt = CreatedAt,
        SessionTokenHashes = null
    };

    public static string MakeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
        var single = parts.Length == 1 ? parts[0] : name.Trim();
        return single.Length >= 2 ? single[..2].ToUpperInvariant() : single.ToUpperInvariant();
    }
}

/// <summary>Root object of an accounts.json file.</summary>
public class AccountStore
{
    public int Version { get; set; } = 1;
    public List<Account> Accounts { get; set; } = new();
}
