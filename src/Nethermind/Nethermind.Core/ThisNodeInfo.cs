// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace Nethermind.Core
{
    public static class ThisNodeInfo
    {
        private static ConcurrentDictionary<string, string> _nodeInfoItems = new();

        public static void AddInfo(string infoDescription, string value)
        {
            _nodeInfoItems.TryAdd(infoDescription, value);
        }

        public static string BuildNodeInfoScreen()
        {
            StringBuilder builder = new();
            builder.AppendLine();
            builder.AppendLine("======================== Nethermind initialization completed ========================");

            foreach ((string key, string value) in _nodeInfoItems.OrderByDescending(ni => ni.Key))
            {
                builder.AppendLine($"{key} {value}");
            }

            builder.Append("=====================================================================================");
            return builder.ToString();
        }
    }
}
