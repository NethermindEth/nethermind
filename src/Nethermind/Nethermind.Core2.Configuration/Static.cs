using Microsoft.Extensions.Options;

namespace Nethermind.Core2.Configuration
{
    public static class Static
    {
        public static IOptionsMonitor<T> OptionsMonitor<T>(T options)
            where T : class, new()
        {
            return new StaticOptionsMonitor<T>(options);
        }
        
        public static IOptionsMonitor<T> OptionsMonitor<T>()
            where T : class, new()
        {
            return new StaticOptionsMonitor<T>(new T());
        }
    }
}