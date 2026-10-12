# Contributing

Keep public interfaces and registration methods readable, with separate
declarations and expanded blocks.

Every service exposes exactly one accessor per target framework. Put the
`IServiceProvider` extension property inside `#if NET10_0_OR_GREATER` and its
`Hive()` extension method inside `#else`. The `net10.0` assembly requires C# 14
and uses the property; `net9.0` uses the method. Resolve through
`GetRequiredService<T>()` each time; never cache resolved services globally.

Build both targets with the .NET 10 SDK. A .NET 9 SDK build produces only
`net9.0`. The library is `IsAotCompatible`: do not add reflection-based
serializers or mappers.
