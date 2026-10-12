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
    Console.WriteLine("PASS hive boxes");
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
