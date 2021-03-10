//  Copyright (c) 2021 Demerzel Solutions Limited
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
