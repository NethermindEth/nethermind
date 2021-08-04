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

namespace Nethermind.Sockets
{
    public class WebSocketsManager : IWebSocketsManager
    {
        private readonly ConcurrentDictionary<string, IWebSocketsModule> _modules = new();

        private IWebSocketsModule _defaultModule = null;

        public void AddModule(IWebSocketsModule module, bool isDefault = false)
        {
            _modules.TryAdd(module.Name, module);
            
            if (isDefault)
            {
                _defaultModule = module;
            }
        }

        public IWebSocketsModule GetModule(string name) => _modules.TryGetValue(name, out var module) ? module : _defaultModule;
    }
}
