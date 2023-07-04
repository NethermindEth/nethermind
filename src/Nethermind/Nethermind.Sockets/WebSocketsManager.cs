// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.Sockets
{
    public class WebSocketsManager : IWebSocketsManager
    {
        private readonly ConcurrentDictionary<string, IWebSocketsModule> _modules = new();

        private IWebSocketsModule _defaultModule = null!;

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
