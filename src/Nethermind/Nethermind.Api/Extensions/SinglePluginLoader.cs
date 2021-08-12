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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;

namespace Nethermind.Api.Extensions
{
    /// <summary>
    /// This class is introduced for easier testing of the plugins under construction - it allows to load a plugin
    /// directly from the current solution
    /// </summary>
    /// <typeparam name="T">Type of the plugin to load</typeparam>
    public class SinglePluginLoader<T> : IPluginLoader where T : INethermindPlugin
    {
        private SinglePluginLoader() { }

        public static IPluginLoader Instance { get; } = new SinglePluginLoader<T>();
        
        public IEnumerable<Type> PluginTypes => Enumerable.Repeat(typeof(T), 1);
        
        public void Load(ILogManager logManager) { }
    }
}
