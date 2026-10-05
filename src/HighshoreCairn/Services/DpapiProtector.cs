using System.Security.Cryptography;
using System.Text;

namespace HighshoreCairn.Services;

/// <summary>
/// Windows DPAPI: the key is managed by Windows and tied to the current Windows user,
/// so the encrypted files can only be read from the same Windows profile.
/// </summary>
public class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HighshoreKanban.v1");

    public byte[] Protect(byte[] plain) =>
        ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] encrypted) =>
        ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
}
