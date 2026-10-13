using System.Text;
using System.Text.Json.Serialization;
using DotNative.Hive;

var root = Path.Combine(Path.GetTempPath(), "dotnative-hive-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new HiveStore(root);
    var plain = await store.OpenBoxAsync("notes");
    plain.Put("a", "alpha"u8);
    Require(plain.Get("a") is { } first && Encoding.UTF8.GetString(first) == "alpha");
    plain.Put("person", new Person("Ada", 36), PersonContext.Default.Person);
    var person = plain.Get("person", PersonContext.Default.Person);
    Require(person is { Name: "Ada", Year: 36 });
    plain.Dispose();

    var again = await store.OpenBoxAsync("notes");
    Require(again.ContainsKey("a") && again.Keys.Count == 2);
    Require(again.Delete("a") && again.Get("a") is null);
    again.Dispose();

    var key = new byte[32];
    key[0] = 7;
    var secret = await store.OpenBoxAsync("user", key);
    secret.Put("token", "hidden"u8);
    secret.Dispose();
    var wrong = key.ToArray();
    wrong[0] = 8;
    await ThrowsAsync(() => store.OpenBoxAsync("user", wrong));
    var opened = await store.OpenBoxAsync("user", key);
    Require(opened.Get("token") is { } token && Encoding.UTF8.GetString(token) == "hidden");
    opened.Clear();
    Require(opened.Keys.Count == 0);
    opened.Dispose();

    Throws(() => store.OpenBoxAsync("..").GetAwaiter().GetResult());
    Throws(() => store.OpenBoxAsync("user", new byte[16]).GetAwaiter().GetResult());
    var legacy = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacy);
    var legacyFile = Path.Combine(legacy, HiveKeys.Hash("old-user") + ".json");
    File.WriteAllText(legacyFile, "{\"Name\":\"Legacy\",\"Year\":42}");
    using (var migrated = await store.OpenBoxAsync("migrated"))
    {
        await migrated.ImportLegacyJsonAsync("profile", legacy, "old-user", PersonContext.Default.Person);
        Require(migrated.Get("profile", PersonContext.Default.Person)?.Name == "Legacy");
        migrated.Put("profile", new Person("New", 1), PersonContext.Default.Person);
        await migrated.ImportLegacyJsonAsync("profile", legacy, "old-user", PersonContext.Default.Person);
        Require(migrated.Get("profile", PersonContext.Default.Person)?.Name == "New" && File.Exists(legacyFile));
    }
    File.WriteAllText(Path.Combine(root, "broken.box"), "broken");
    await ThrowsAsync(() => store.OpenBoxAsync("broken"));
    var recovering = new HiveStore(root, recoverCorruptBoxes: true);
    using (var recovered = await recovering.OpenBoxAsync("broken"))
    {
        Require(recovered.Keys.Count == 0);
        recovered.Put("ok", "recovered"u8);
    }
    Require(Directory.GetFiles(root, "broken.box.corrupt-*").Length == 1);
    // Recovery must never discard encrypted data because a key was omitted.
    await ThrowsAsync(() => recovering.OpenBoxAsync("user"));

    using (var rollback = await store.OpenBoxAsync("rollback"))
    {
        rollback.Put("keep", "old"u8);
        var file = Path.Combine(root, "rollback.box");
        File.Move(file, file + ".backup");
        Directory.CreateDirectory(file); // Force the atomic rename to fail.
        try
        {
            try { rollback.Put("keep", "new"u8); throw new Exception("Write unexpectedly succeeded"); }
            catch (IOException) { }
            Require(Encoding.UTF8.GetString(rollback.Get("keep")!) == "old");
            try { rollback.Delete("keep"); throw new Exception("Delete unexpectedly succeeded"); }
            catch (IOException) { }
            Require(rollback.ContainsKey("keep"));
            try { rollback.Clear(); throw new Exception("Clear unexpectedly succeeded"); }
            catch (IOException) { }
            Require(rollback.ContainsKey("keep"));
        }
        finally { Directory.Delete(file); File.Move(file + ".backup", file); }
    }
    Require(Directory.GetFiles(root, "*.tmp").Length == 0);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await store.OpenBoxAsync("cancelled", cancellationToken: cancelled.Token); throw new Exception("Ignored cancellation"); }
        catch (OperationCanceledException) { }
        Require(!File.Exists(Path.Combine(root, "cancelled.box")));
    }
    Console.WriteLine("PASS hive boxes, JSON migration, corruption recovery and atomic-write rollback");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Require(bool condition)
{
    if (!condition)
        throw new Exception("Assertion failed");
}

static void Throws(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }
    throw new Exception("Expected ArgumentException");
}

static async Task ThrowsAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new Exception("Expected InvalidDataException");
}

sealed record Person(string Name, int Year);

[JsonSerializable(typeof(Person))]
sealed partial class PersonContext : JsonSerializerContext;
