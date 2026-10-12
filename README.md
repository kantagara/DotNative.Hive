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
