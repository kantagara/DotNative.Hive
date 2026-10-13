using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.Hive;

public interface IHive
{
    Task<IHiveBox> OpenBoxAsync(
        string name,
        ReadOnlyMemory<byte> encryptionKey = default,
        CancellationToken cancellationToken = default
    );
}

public interface IHiveBox : IDisposable
{
    void Put(string key, ReadOnlySpan<byte> value);
    byte[]? Get(string key);
    bool Delete(string key);
    bool ContainsKey(string key);
    IReadOnlyCollection<string> Keys { get; }
    void Clear();
    void Put<T>(string key, T value, JsonTypeInfo<T> type);
    T? Get<T>(string key, JsonTypeInfo<T> type);
}

public sealed class HiveStore : IHive
{
    private readonly string root;
    private readonly bool recoverCorruptBoxes;
    private readonly object gate = new();
    private readonly Dictionary<string, HiveBox> open = new(StringComparer.Ordinal);

    public HiveStore(string directory, bool recoverCorruptBoxes = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Box directory must be a full path.", nameof(directory));
        Directory.CreateDirectory(directory);
        root = directory;
        this.recoverCorruptBoxes = recoverCorruptBoxes;
    }

    public Task<IHiveBox> OpenBoxAsync(
        string name,
        ReadOnlyMemory<byte> encryptionKey = default,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safe = BoxName(name);
        var key = encryptionKey.Length == 0 ? null : encryptionKey.ToArray();
        if (key is not null && key.Length != 32)
            throw new ArgumentException("The encryption key has to be 32 bytes.", nameof(encryptionKey));
        lock (gate)
        {
            if (open.TryGetValue(safe, out var existing))
            {
                if (existing.TryRetain(key))
                    return Task.FromResult<IHiveBox>(existing);
                open.Remove(safe);
            }
            var path = Path.Combine(root, safe + ".box");
            HiveBox box;
            try
            {
                box = HiveBox.Open(this, safe, path, key);
            }
            catch (InvalidDataException) when (recoverCorruptBoxes && key is null && !HasEncryptedHeader(path))
            {
                File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                box = HiveBox.Open(this, safe, path, key);
            }
            open[safe] = box;
            return Task.FromResult<IHiveBox>(box);
        }
    }

    private static bool HasEncryptedHeader(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[6];
        return file.Read(header) == 6 && header[..4].SequenceEqual("DNHV"u8) && header[5] == 1;
    }

    internal void Release(string name, HiveBox box)
    {
        lock (gate)
        {
            if (open.TryGetValue(name, out var current) && ReferenceEquals(current, box))
                open.Remove(name);
        }
    }

    internal static string BoxName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            throw new ArgumentException(
                "Box name must be 1 to 64 letters, digits, '_' or '-'.",
                nameof(name)
            );
        foreach (var character in name)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
                throw new ArgumentException(
                    "Box name must be 1 to 64 letters, digits, '_' or '-'.",
                    nameof(name)
                );
        return name;
    }
}

internal sealed class HiveBox : IHiveBox
{
    private const int MaxEntries = 10_000;
    private const int MaxBlob = 32 * 1024 * 1024;
    private readonly HiveStore store;
    private readonly string name;
    private readonly string path;
    private readonly byte[]? key;
    private readonly Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private int references = 1;
    private bool disposed;

    private HiveBox(HiveStore store, string name, string path, byte[]? key)
    {
        this.store = store;
        this.name = name;
        this.path = path;
        this.key = key;
    }

    public static HiveBox Open(HiveStore store, string name, string path, byte[]? key)
    {
        var box = new HiveBox(store, name, path, key);
        if (File.Exists(path))
            box.Read();
        else
            box.Write();
        return box;
    }

    public bool TryRetain(byte[]? otherKey)
    {
        lock (gate)
        {
            if (disposed)
                return false;
            if (!KeysEqual(key, otherKey))
                throw new InvalidDataException("Box is already open with a different key.");
            references++;
            return true;
        }
    }

    public void Put(string key, ReadOnlySpan<byte> value)
    {
        EntryKey(key);
        if (value.Length > MaxBlob)
            throw new ArgumentException("Value is larger than 32 MiB.", nameof(value));
        lock (gate)
        {
            EnsureOpen();
            if (!entries.ContainsKey(key) && entries.Count >= MaxEntries)
                throw new InvalidOperationException("Box entry limit reached.");
            var existed = entries.TryGetValue(key, out var previous);
            entries[key] = value.ToArray();
            try { Write(); }
            catch
            {
                if (existed) entries[key] = previous!;
                else entries.Remove(key);
                throw;
            }
        }
    }

    public byte[]? Get(string key)
    {
        EntryKey(key);
        lock (gate)
        {
            EnsureOpen();
            return entries.TryGetValue(key, out var value) ? value.ToArray() : null;
        }
    }

    public bool Delete(string key)
    {
        EntryKey(key);
        lock (gate)
        {
            EnsureOpen();
            if (!entries.Remove(key, out var previous))
                return false;
            try { Write(); }
            catch { entries[key] = previous; throw; }
            return true;
        }
    }

    public bool ContainsKey(string key)
    {
        EntryKey(key);
        lock (gate)
        {
            EnsureOpen();
            return entries.ContainsKey(key);
        }
    }

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (gate)
            {
                EnsureOpen();
                return entries.Keys.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            EnsureOpen();
            if (entries.Count == 0)
                return;
            var previous = entries.ToArray();
            entries.Clear();
            try { Write(); }
            catch
            {
                foreach (var item in previous) entries.Add(item.Key, item.Value);
                throw;
            }
        }
    }

    public void Put<T>(string key, T value, JsonTypeInfo<T> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        Put(key, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type));
    }

    public T? Get<T>(string key, JsonTypeInfo<T> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var bytes = Get(key);
        return bytes is null ? default : System.Text.Json.JsonSerializer.Deserialize(bytes, type);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed || --references > 0)
                return;
            disposed = true;
        }
        store.Release(name, this);
    }

    private void EnsureOpen()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(IHiveBox));
    }

    private void Read()
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual("DNHV"u8) || bytes[4] != 1)
            throw new InvalidDataException("Box file is not a DotNative Hive box.");
        var encrypted = bytes[5] == 1;
        if (bytes[5] > 1)
            throw new InvalidDataException("Box file is not a DotNative Hive box.");
        if (encrypted != (key is not null))
            throw new InvalidDataException(
                encrypted ? "Box is encrypted." : "Box key was supplied for a plaintext box."
            );
        var cursor = 6;
        if (encrypted)
        {
            if (bytes.Length < cursor + 32)
                throw new InvalidDataException("Box file is truncated.");
            if (!bytes.AsSpan(cursor, 32).SequenceEqual(SHA256.HashData(key!)))
                throw new InvalidDataException("Box key does not match.");
            cursor += 32;
        }
        var count = ReadInt(bytes, ref cursor);
        if (count < 0 || count > MaxEntries)
            throw new InvalidDataException("Box file is not a DotNative Hive box.");
        for (var index = 0; index < count; index++)
        {
            var storedKey = ReadBlob(bytes, ref cursor);
            var storedValue = ReadBlob(bytes, ref cursor);
            if (encrypted)
            {
                storedKey = HiveCipher.Decrypt(key!, storedKey);
                storedValue = HiveCipher.Decrypt(key!, storedValue);
            }
            var text = Encoding.UTF8.GetString(storedKey);
            EntryKey(text);
            entries[text] = storedValue;
        }
        if (cursor != bytes.Length)
            throw new InvalidDataException("Box file is not a DotNative Hive box.");
    }

    private void Write()
    {
        using var stream = new MemoryStream();
        stream.Write("DNHV"u8);
        stream.WriteByte(1);
        stream.WriteByte(key is null ? (byte)0 : (byte)1);
        if (key is not null)
            stream.Write(SHA256.HashData(key));
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, entries.Count);
        stream.Write(count);
        foreach (var (entryKey, entryValue) in entries)
        {
            var keyBytes = Encoding.UTF8.GetBytes(entryKey);
            if (key is not null)
            {
                WriteBlob(stream, HiveCipher.Encrypt(key, keyBytes));
                WriteBlob(stream, HiveCipher.Encrypt(key, entryValue));
            }
            else
            {
                WriteBlob(stream, keyBytes);
                WriteBlob(stream, entryValue);
            }
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Position = 0;
                stream.CopyTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static int ReadInt(byte[] bytes, ref int cursor)
    {
        if (bytes.Length - cursor < 4)
            throw new InvalidDataException("Box file is truncated.");
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(cursor));
        cursor += 4;
        return value;
    }

    private static byte[] ReadBlob(byte[] bytes, ref int cursor)
    {
        var length = ReadInt(bytes, ref cursor);
        if (length < 0 || length > MaxBlob || bytes.Length - cursor < length)
            throw new InvalidDataException("Box file is truncated.");
        var blob = bytes.AsSpan(cursor, length).ToArray();
        cursor += length;
        return blob;
    }

    private static void WriteBlob(Stream stream, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void EntryKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512)
            throw new ArgumentException("Entry key must be 1 to 512 characters.", nameof(key));
    }

    private static bool KeysEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && CryptographicOperations.FixedTimeEquals(left, right);
}

internal static class HiveCipher
{
    public static byte[] Encrypt(byte[] key, ReadOnlySpan<byte> plain)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var cipher = aes.CreateEncryptor().TransformFinalBlock(plain.ToArray(), 0, plain.Length);
        var result = new byte[iv.Length + cipher.Length];
        iv.CopyTo(result, 0);
        cipher.CopyTo(result, iv.Length);
        return result;
    }

    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 32)
            throw new InvalidDataException("Encrypted box entry is truncated.");
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = payload[..16].ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        try
        {
            return aes.CreateDecryptor().TransformFinalBlock(payload[16..].ToArray(), 0, payload.Length - 16);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("Box key does not match.", exception);
        }
    }
}

public static class HiveServices
{
    public static IServiceCollection AddHive(this IServiceCollection services, string directory, bool recoverCorruptBoxes = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IHive>(_ => new HiveStore(directory, recoverCorruptBoxes));
        return services;
    }
}
