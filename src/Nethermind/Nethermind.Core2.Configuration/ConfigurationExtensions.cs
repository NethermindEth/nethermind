// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.Configuration;

namespace Nethermind.Core2.Configuration
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
            var bytes = Bytes.FromHexString(hex);
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
