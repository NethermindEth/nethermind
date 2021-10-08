//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
