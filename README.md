# DotNative.Hive

Named local boxes for the same job as Flutter Hive: a string key, a value, and
an optional 32-byte AES key kept in secure storage.

```csharp
builder.Services.AddHive(boxDirectory);
var chats = await services.Hive.OpenBoxAsync("chats", encryptionKey);
chats.Put("last", "{\"id\":7}"u8);
var last = chats.Get("last");
```

Objects go through `System.Text.Json` source generation:

```csharp
chats.Put("user", user, AppJsonContext.Default.UserData);
var user = chats.Get("user", AppJsonContext.Default.UserData);
```

`OpenBoxAsync` loads the whole box. `Put`, `Get`, `Delete`, and `Clear` then
run against that copy and rewrite the file. A second open of the same name
returns the same box and must use the same key. Dispose when finished.

The key is 32 bytes, or omitted for a plaintext box. AES-256-CBC with PKCS7 and
a random 16-byte IV matches the Hive cipher envelope. The on-disk layout is
this package's `DNHV` file, so an existing Flutter `.hive` file does not open
here.

LiteDB is the nearest NuGet document store. It is not referenced: its mapper is
not NativeAOT-safe, and its database is a different file.

## Service access

Register `AddHive(directory)` before building the provider. `directory` is a
full path. `services.Hive` is the C# 14 property on `net10.0`.
`services.Hive()` is the method on `net9.0`.


## Persistent cache integration

This is an independent managed plugin. `dotnative/plugin.json` is included in the
NuGet package; no sample or UI runtime dependency is required. JSON payloads use
source-generated `JsonTypeInfo<T>` for NativeAOT. Box IO is synchronous even though
opening returns a Task; invoke it on a worker when using a UI thread.

Writes flush a unique temporary file before atomic replacement. A failed `Put`,
`Delete` or `Clear` restores the in-memory state as well. Durability across power
loss still depends on the filesystem. Use one `HiveStore` per directory; independent
stores/processes do not coordinate read-modify-write operations.

`AddHive(directory, recoverCorruptBoxes: true)` optionally quarantines corrupt
plaintext boxes as `.box.corrupt-<id>` and creates empty replacements. The default
remains strict. Encrypted boxes are never reset for a missing/mismatched key.

`IHiveBox.ImportLegacyJsonAsync` imports a SHA-256-named JSON file only when the
destination key is absent. It validates with the supplied `JsonTypeInfo<T>`, keeps
the source for rollback, and limits imported files to 16 MiB. The caller serializes
migration with writes to that key. `HiveKeys.Hash` supplies the stable filename hash.
This is a migration helper, not Flutter Hive binary-file compatibility.

App code owns schemas, account scoping, TTL, eviction, offline merge rules and retry
logic. These do not belong to the storage plugin. Plaintext chat caches need no
key; credentials belong in secure storage.

Build/test/package locally:

```sh
dotnet run --project tests/DotNative.Hive.Tests
dotnet pack DotNative.Hive.csproj -c Release -o artifacts
```
