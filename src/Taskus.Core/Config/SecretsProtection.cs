using System.IO.Abstractions;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Taskus.Config;

/// <summary>
/// Replaces <c>secrets.yaml</c> with <c>secrets.sec</c> encrypted by Windows DPAPI, and back. DPAPI keeps the key in
/// Windows: the file opens only on the same machine (and, with the user scope, only for the same account).
/// </summary>
public static class SecretsProtection
{
    private static readonly byte[] Entropy = "taskus"u8.ToArray();

    [SupportedOSPlatform("windows")]
    public static Func<byte[], byte[]> Protector(DataProtectionScope scope) => data => ProtectedData.Protect(data, Entropy, scope);

    // The scope is stored in the protected data, so one call reads files of both scopes.
    [SupportedOSPlatform("windows")]
    public static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);

    /// <summary>Writes the protected copy and deletes the plain file, but only after the copy reads back the same.</summary>
    public static Result<string> Protect(IFileSystem fs, string file, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        if (!fs.File.Exists(file))
            return Result.Failure<string>($"{file}: no such file");

        var text = fs.File.ReadAllText(file);
        var parsed = Secrets.Parse(text, file);
        if (parsed.IsFailure)
            return Result.Failure<string>(parsed.Error);

        var protectedFile = Secrets.ProtectedFile(file);
        fs.File.WriteAllBytes(protectedFile, protect(Encoding.UTF8.GetBytes(text)));
        var check = Secrets.Decrypt(fs, protectedFile, unprotect);
        if (check.IsFailure || check.Value != text)
            return Result.Failure<string>($"{protectedFile} does not read back the same as {file}; {file} is kept");

        fs.File.Delete(file);
        return protectedFile;
    }

    /// <summary>Writes the plain file for editing and keeps the protected copy, so runs fail until it is protected again.</summary>
    public static Result<string> Unprotect(IFileSystem fs, string file, Func<byte[], byte[]> unprotect)
    {
        var protectedFile = Secrets.ProtectedFile(file);
        if (!fs.File.Exists(protectedFile))
            return Result.Failure<string>($"{protectedFile}: no such file");
        if (fs.File.Exists(file))
            return Result.Failure<string>($"{file} already exists; delete it or run 'taskus secrets protect'");

        return Secrets.Decrypt(fs, protectedFile, unprotect).Map(text =>
        {
            fs.File.WriteAllText(file, text);
            return file;
        });
    }
}
