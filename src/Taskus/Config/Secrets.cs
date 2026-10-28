using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Taskus.Config;

/// <summary>Loads <c>secrets.yaml</c> (or its protected copy <c>secrets.sec</c>) and replaces <c>${secret:name}</c> references in a task tree.</summary>
public static partial class Secrets
{
    [GeneratedRegex(@"\$\{secret:([^}]+)\}")]
    private static partial Regex Reference();

    public static JsonObject None => [];

    /// <summary>The protected copy of <paramref name="file"/>: same name, extension <c>.sec</c>.</summary>
    public static string ProtectedFile(string file) => Path.ChangeExtension(file, ".sec");

    /// <summary>
    /// Loads secrets from <paramref name="file"/> or its protected copy. With <paramref name="unprotect"/> (Windows)
    /// only the protected copy is read, and a plain file next to it is an error. Without it (no DPAPI) the plain file
    /// is read. No file gives no secrets: tasks without references still run.
    /// </summary>
    public static Result<JsonObject> Load(IFileSystem fs, string file, Func<byte[], byte[]>? unprotect = null)
    {
        var protectedFile = ProtectedFile(file);
        var hasPlain = fs.File.Exists(file);
        var hasProtected = fs.File.Exists(protectedFile);

        if (unprotect is null)
        {
            if (hasPlain)
                return Parse(fs.File.ReadAllText(file), file);
            return hasProtected
                ? Result.Failure<JsonObject>($"{protectedFile}: protected secrets can be read only on Windows; use {file} here")
                : None;
        }

        if (hasPlain && hasProtected)
            return Result.Failure<JsonObject>(
                $"Both {protectedFile} and {file} exist. Delete the unprotected {file} (run 'taskus secrets protect' first if you changed it)");
        if (hasPlain)
            return Result.Failure<JsonObject>($"{file} is not protected. Run 'taskus secrets protect' to replace it with {protectedFile}");
        if (!hasProtected)
            return None;

        return Decrypt(fs, protectedFile, unprotect).Bind(text => Parse(text, protectedFile));
    }

    /// <summary>Reads a protected file as text; a file from another machine or account gives a failure, not an exception.</summary>
    public static Result<string> Decrypt(IFileSystem fs, string protectedFile, Func<byte[], byte[]> unprotect)
    {
        try
        {
            return Encoding.UTF8.GetString(unprotect(fs.File.ReadAllBytes(protectedFile)));
        }
        catch (CryptographicException)
        {
            return Result.Failure<string>(
                $"{protectedFile}: cannot be decrypted. It was protected on another machine, by another user account " +
                "(--current-user), or it is damaged. Create the plain file again and run 'taskus secrets protect'");
        }
    }

    public static Result<JsonObject> Parse(string text, string file) =>
        YamlJson.Parse(text).MapError(error => $"{file}: {error}").Bind(tree => tree switch
        {
            null => Result.Success(None),
            JsonObject map => Result.Success(map),
            _ => Result.Failure<JsonObject>($"{file}: must be a mapping of name to value"),
        });

    /// <summary>Returns a copy of <paramref name="node"/> with every reference replaced.</summary>
    public static Result<JsonNode?> Resolve(JsonNode? node, JsonObject secrets)
    {
        switch (node)
        {
            case JsonObject map:
                var resolvedMap = new JsonObject();
                foreach (var (key, value) in map)
                {
                    var resolved = Resolve(value, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedMap[key] = resolved.Value;
                }

                return resolvedMap;
            case JsonArray list:
                var resolvedList = new JsonArray();
                foreach (var item in list)
                {
                    var resolved = Resolve(item, secrets);
                    if (resolved.IsFailure)
                        return resolved;
                    resolvedList.Add(resolved.Value);
                }

                return resolvedList;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return ResolveText(text, secrets);
            default:
                return Result.Success(node?.DeepClone());
        }
    }

    // A value that is only a reference takes the secret as is, so a secret can be a whole mapping (smtp).
    // A reference inside longer text must point to a text secret.
    private static Result<JsonNode?> ResolveText(string text, JsonObject secrets)
    {
        var whole = Reference().Match(text);
        if (whole.Success && whole.Length == text.Length)
            return Lookup(whole.Groups[1].Value, secrets).Map(secret => secret?.DeepClone());

        string? error = null;
        var replaced = Reference().Replace(text, m =>
        {
            var secret = Lookup(m.Groups[1].Value, secrets);
            if (secret is { IsSuccess: true, Value: JsonValue value } && value.TryGetValue<string>(out var secretText))
                return secretText;
            error ??= secret.IsFailure ? secret.Error : $"secret '{m.Groups[1].Value}' is not text and cannot be part of a longer value";
            return m.Value;
        });

        return error is null ? JsonValue.Create(replaced) : Result.Failure<JsonNode?>(error);
    }

    private static Result<JsonNode?> Lookup(string name, JsonObject secrets) =>
        secrets.TryGetPropertyValue(name, out var value)
            ? Result.Success(value)
            : Result.Failure<JsonNode?>($"secret '{name}' is not defined in secrets.yaml");
}
