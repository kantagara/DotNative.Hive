using System;
using Microsoft.Extensions.DependencyInjection;

namespace DotNative.Hive;

public static class HiveServiceProviderExtensions
{
#if NET10_0_OR_GREATER
    extension(IServiceProvider services)
    {
        /// <summary>Resolves the registered boxes using the provider's DI lifetime.</summary>
        public IHive Hive => services.GetRequiredService<IHive>();
    }
#else
    /// <summary>Resolves the registered boxes using the provider's DI lifetime.</summary>
    public static IHive Hive(this IServiceProvider services) => services.GetRequiredService<IHive>();
#endif
}
