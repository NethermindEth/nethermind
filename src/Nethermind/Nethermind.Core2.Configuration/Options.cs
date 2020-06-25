using Microsoft.Extensions.Options;

namespace Nethermind.Core2.Configuration
{
    public static class Options
    {
        public static IOptionsMonitor<T> Use<T>(T options)
            where T : class, new()
        {
            return new StaticOptionsMonitor<T>(options);
        }
        
        public static IOptionsMonitor<T> Default<T>()
            where T : class, new()
        {
            return new StaticOptionsMonitor<T>(new T());
        }
    }
}