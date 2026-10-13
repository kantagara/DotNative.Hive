using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNative.Hive;

public static class HiveKeys
{
    public static string Hash(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

public static class HiveJsonMigration
{
    /// <summary>
    /// Imports a legacy SHA-256-named JSON value if the destination key is missing.
    /// Leaves the source file intact for rollback and never replaces an existing value.
    /// Callers should serialize migration against writes to the same destination key.
    /// </summary>
    public static async Task ImportLegacyJsonAsync<T>(
        this IHiveBox box,
        string key,
        string directory,
        string legacyKey,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        if (box.ContainsKey(key)) return;
        var path = Path.Combine(directory, HiveKeys.Hash(legacyKey) + ".json");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Legacy JSON value exceeds 16 MiB.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize(bytes, type);
        cancellationToken.ThrowIfCancellationRequested();
        if (value is not null && !box.ContainsKey(key)) box.Put(key, value, type);
    }
}
