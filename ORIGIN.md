# Source origin

This package is an independently authored DotNative box store. Flutter Hive has
no maintained .NET port of its frame file. LiteDB is the closest NuGet document
database, and it is not used here: its mapper and expression compiler are not
NativeAOT-safe, and its file is not a Hive box.

Values are caller-serialized bytes. `System.Text.Json` source generation is the
supported object codec. Optional encryption is AES-256-CBC with PKCS7 padding
and a random 16-byte IV in front of the ciphertext, the same envelope as
`HiveAesCipher`. The box file itself is this package's `DNHV` layout, not a
Flutter `.hive` file.
