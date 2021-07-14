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
// 

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.JsonRpc
{
    public static class JsonRpcConfigExtension
    {
        public static void EnableModules(this IJsonRpcConfig config, params string[] modules)
        {
            HashSet<string> enabledModules = config.EnabledModules.ToHashSet();
            for (int i = 0; i < modules.Length; i++)
            {
                enabledModules.Add(modules[i]);
            }
            config.EnabledModules = enabledModules.ToArray();
        }
    }
}
