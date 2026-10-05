using HighshoreCairn.Models;

namespace HighshoreCairn.Services;

/// <summary>
/// Global account archive of this PC (encrypted accounts.json) + local session (auto-login token).
/// </summary>
public class AccountService
{
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 32;
    public const int MinPasswordLength = 6;

    private readonly ProtectedFile _file;
    private AccountStore _store = new();

    public AccountService(IDataProtector protector) => _file = new ProtectedFile(protector);

    public IReadOnlyList<Account> Accounts => _store.Accounts;

    /// <summary>The account currently logged in (null when nobody is).</summary>
    public Account? Current { get; private set; }

    public event Action? CurrentChanged;

    private class SessionData
    {
        public string AccountId { get; set; } = "";
        public string Token { get; set; } = "";
    }

    // ------------------------------------------------------------------ storage

    public void Load()
    {
        try
        {
            _store = _file.Load<AccountStore>(AppPaths.AccountsFile) ?? new AccountStore();
        }
        catch (Exception)
        {
            // Unreadable archive (e.g. copied from another Windows profile): keep it aside, start clean.
            TryQuarantine(AppPaths.AccountsFile);
            _store = new AccountStore();
        }
    }

    private void Save() => _file.Save(AppPaths.AccountsFile, _store);

    private static void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
        }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------ queries

    public Account? FindById(string? id) =>
        id is null ? null : _store.Accounts.FirstOrDefault(a => a.Id == id);

    public Account? FindByUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null
            : _store.Accounts.FirstOrDefault(a => string.Equals(a.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ validation

    /// <summary>Returns an error message, or null when the values are acceptable.</summary>
    public string? ValidateProfile(string username, string? email, string? ignoreAccountId = null)
    {
        username = (username ?? "").Trim();
        if (username.Length < MinUsernameLength || username.Length > MaxUsernameLength)
            return $"Username must be {MinUsernameLength}-{MaxUsernameLength} characters long.";
        var existing = FindByUsername(username);
        if (existing != null && existing.Id != ignoreAccountId)
            return $"The username '{username}' is already taken on this PC.";
        if (!string.IsNullOrWhiteSpace(email) && (!email.Contains('@') || email.Trim().Contains(' ')))
            return "The email address does not look valid.";
        return null;
    }

    public static string? ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Password must be at least {MinPasswordLength} characters long.";
        return null;
    }

    // ------------------------------------------------------------------ CRUD

    public Account Create(string username, string password, string? email, string avatarColor, string? avatarImage = null)
    {
        var error = ValidateProfile(username, email) ?? ValidatePassword(password);
        if (error != null) throw new InvalidOperationException(error);

        var account = new Account
        {
            Username = username.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            AvatarColor = avatarColor,
            AvatarImage = string.IsNullOrEmpty(avatarImage) ? null : avatarImage,
            SessionTokenHashes = new List<string>()
        };
        PasswordHasher.SetPassword(account, password);
        _store.Accounts.Add(account);
        Save();
        return account;
    }

    /// <summary>Updates the profile; the password changes only when <paramref name="newPassword"/> is not empty.</summary>
    public void Update(Account account, string username, string? email, string avatarColor, string? newPassword) =>
        Update(account, username, email, avatarColor, newPassword, account.AvatarImage);

    /// <summary>Same as above, also replacing the profile picture (null removes it).</summary>
    public void Update(Account account, string username, string? email, string avatarColor, string? newPassword, string? avatarImage)
    {
        var error = ValidateProfile(username, email, account.Id);
        if (error == null && !string.IsNullOrEmpty(newPassword)) error = ValidatePassword(newPassword);
        if (error != null) throw new InvalidOperationException(error);

        account.Username = username.Trim();
        account.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        account.AvatarColor = avatarColor;
        account.AvatarImage = string.IsNullOrEmpty(avatarImage) ? null : avatarImage;
        if (!string.IsNullOrEmpty(newPassword))
        {
            PasswordHasher.SetPassword(account, newPassword);
            // A password change invalidates every auto-login token except the current session.
            account.SessionTokenHashes = new List<string>();
            if (Current?.Id == account.Id) StartSession(account);
        }
        Save();
        if (Current?.Id == account.Id) CurrentChanged?.Invoke();
    }

    public void Delete(Account account)
    {
        _store.Accounts.RemoveAll(a => a.Id == account.Id);
        Save();
        if (Current?.Id == account.Id) Logout();
    }

    /// <summary>
    /// Adds to the global archive an account that comes from a project folder
    /// (e.g. a project cloned from git) and returns the global instance.
    /// </summary>
    public Account Import(Account projectAccount)
    {
        var existing = FindById(projectAccount.Id);
        if (existing != null) return existing;

        var copy = projectAccount.CloneForProject();
        copy.SessionTokenHashes = new List<string>();
        // Avoid duplicate usernames in the local archive.
        var baseName = copy.Username;
        var n = 2;
        while (FindByUsername(copy.Username) != null) copy.Username = $"{baseName}-{n++}";
        _store.Accounts.Add(copy);
        Save();
        return copy;
    }

    // ------------------------------------------------------------------ login / session

    /// <summary>Checks the credentials and, when valid, logs the account in and stores the session token.</summary>
    public Account? Login(string username, string password)
    {
        var account = FindByUsername(username);
        if (account is null || !PasswordHasher.Verify(account, password)) return null;
        StartSession(account);
        return account;
    }

    public bool VerifyPassword(Account account, string password) => PasswordHasher.Verify(account, password);

    /// <summary>Marks the account as logged in and writes the encrypted session file used for auto-login.</summary>
    public void StartSession(Account account)
    {
        var token = PasswordHasher.NewToken();
        account.SessionTokenHashes ??= new List<string>();
        account.SessionTokenHashes.Add(PasswordHasher.Sha256Hex(token));
        while (account.SessionTokenHashes.Count > 3) account.SessionTokenHashes.RemoveAt(0);
        Save();

        try
        {
            _file.Save(AppPaths.SessionFile, new SessionData { AccountId = account.Id, Token = token });
        }
        catch { /* auto-login is a convenience: the login itself still succeeds */ }

        Current = account;
        CurrentChanged?.Invoke();
    }

    /// <summary>Auto-login: returns the account whose session token is stored on this PC, if still valid.</summary>
    public Account? TryAutoLogin()
    {
        try
        {
            var session = _file.Load<SessionData>(AppPaths.SessionFile);
            if (session is null) return null;
            var account = FindById(session.AccountId);
            var hash = PasswordHasher.Sha256Hex(session.Token);
            if (account?.SessionTokenHashes is null || !account.SessionTokenHashes.Contains(hash))
            {
                DeleteSessionFile();
                return null;
            }
            Current = account;
            CurrentChanged?.Invoke();
            return account;
        }
        catch
        {
            DeleteSessionFile();
            return null;
        }
    }

    public void Logout()
    {
        try
        {
            var session = _file.Load<SessionData>(AppPaths.SessionFile);
            if (session != null)
            {
                var account = FindById(session.AccountId);
                if (account?.SessionTokenHashes != null &&
                    account.SessionTokenHashes.Remove(PasswordHasher.Sha256Hex(session.Token)))
                    Save();
            }
        }
        catch { /* ignore: the file is removed below anyway */ }

        DeleteSessionFile();
        Current = null;
        CurrentChanged?.Invoke();
    }

    private static void DeleteSessionFile()
    {
        try { if (File.Exists(AppPaths.SessionFile)) File.Delete(AppPaths.SessionFile); }
        catch { /* best effort */ }
    }
}
