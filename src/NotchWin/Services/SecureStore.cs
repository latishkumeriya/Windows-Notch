using System.Security.Cryptography;

namespace NotchWin.Services;

/// <summary>Windows DPAPI encryption bound to the current Windows user account.</summary>
public static class SecureStore
{
    static readonly byte[] Entropy = "NotchWin.clipboard.v1"u8.ToArray();

    public static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    /// <summary>Write-then-rename so a crash mid-write never leaves a corrupt file.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }
}
