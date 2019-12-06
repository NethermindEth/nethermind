using System;
using Microsoft.Extensions.Configuration;

namespace Nethermind.BeaconNode
{
    public static class ConfigurationExtensions
    {
        public static void Bind(this IConfiguration configuration, string key, Action<IConfiguration> bindSection)
        {
            var configurationSection = configuration.GetSection(key);
            bindSection(configurationSection);
        }

        public static byte[] GetBytesFromPrefixedHex(this IConfiguration configuration, string key)
        {
            var hex = configuration.GetValue<string>(key);
            if (string.IsNullOrWhiteSpace(hex))
            {
                return Array.Empty<byte>();
            }

            var bytes = new byte[(hex.Length - 2) / 2];
            var hexIndex = 2;
            for (var byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
            {
                bytes[byteIndex] = Convert.ToByte(hex.Substring(hexIndex, 2), 16);
                hexIndex += 2;
            }
            return bytes;
        }

        public static byte[] GetBytesFromPrefixedHex(this IConfiguration configuration, string key, Func<byte[]> defaultValue)
        {
            if (configuration.GetSection(key).Exists())
            {
                return configuration.GetBytesFromPrefixedHex(key);
            }
            else
            {
                return defaultValue();
            }
        }

        public static T GetValue<T>(this IConfiguration configuration, string key, Func<T> defaultValue)
        {
            if (configuration.GetSection(key).Exists())
            {
                return configuration.GetValue<T>(key);
            }
            else
            {
                return defaultValue();
            }
        }
    }
}
