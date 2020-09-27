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
// 

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Api.Extensions
{
    public class PluginManager : IPluginManager
    {
        private readonly INethermindApi _nethermindApi;
        private List<INethermindPlugin> _plugins { get; } = new List<INethermindPlugin>();

        public PluginManager(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
        }
        
        public IReadOnlyCollection<T> Get<T>() where T : INethermindPlugin
        {
            return _plugins.OfType<T>().ToArray();
        }

        public void Register<T>() where T : INethermindPlugin
        {
            Register(typeof(T));
        }

        public void Register(Type type)
        {
            if (!typeof(INethermindPlugin).IsAssignableFrom(type))
            {
                throw new ArgumentException($"Type {type.Name} doe snot implement {nameof(INethermindPlugin)} interface");
            }
            
            INethermindPlugin plugin = (INethermindPlugin)Activator.CreateInstance(type)!;
            plugin.Init(_nethermindApi);
            _plugins.Add(plugin);
        }

        public void Register(INethermindPlugin plugin)
        {
            _plugins.Add(plugin);
        }
    }
}