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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nethermind.Core
{
    public static class ThisNodeInfo
    {
        private static ConcurrentDictionary<string, string> _nodeInfoItems = new ConcurrentDictionary<string, string>();

        private static object _totalMemLock = new object();

        static ThisNodeInfo()
        {
            _nodeInfoItems["Mem est      :"] = "0MB".PadLeft(8);
        }
        
        public static void AddInfo(string infoDescription, string value)
        {
            _nodeInfoItems[infoDescription] = value;

            if (infoDescription.Contains("Mem est"))
            {
                lock (_totalMemLock)
                {
                    int total = 0;
                    foreach (KeyValuePair<string,string> nodeInfoItem in _nodeInfoItems)
                    {
                        if (nodeInfoItem.Key.Contains("Mem est") && nodeInfoItem.Key != "Mem est      :")
                        {
                            total += int.Parse(nodeInfoItem.Value.Trim().Replace("MB", string.Empty));        
                        }
                    }
                    
                    _nodeInfoItems["Mem est      :"] = $"{total}MB".PadLeft(8);
                }
            }
        }

        public static string BuildNodeInfoScreen()
        {
            StringBuilder builder = new StringBuilder();
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