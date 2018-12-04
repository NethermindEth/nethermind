/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Debug;
using Nethermind.JsonRpc.Eth;
using Nethermind.JsonRpc.Module;
using Nethermind.JsonRpc.Net;
using Nethermind.JsonRpc.Trace;
using NSubstitute;

namespace Nethermind.JsonRpc.Test
{
    internal class TestRpcModuleProvider<T> : IRpcModuleProvider where T : class, IModule
    {
        private List<ModuleInfo> _modules = new List<ModuleInfo>();

        public TestRpcModuleProvider(T module)
        {
            _modules.AddRange(new[]
            {
                new ModuleInfo(ModuleType.Net, typeof(INetModule), typeof(INetModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Eth, typeof(IEthModule), typeof(IEthModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Web3, typeof(IWeb3Module), typeof(IWeb3Module).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Nethm, typeof(INethmModule), typeof(INethmModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Debug, typeof(IDebugModule), typeof(IDebugModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Trace, typeof(ITraceModule), typeof(ITraceModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>())
            });
        }

        public void Register<TOther>(IModule module) where TOther : IModule
        {
            ModuleInfo mi = _modules.SingleOrDefault(m => m.ModuleType == module.ModuleType);
            if (mi != null)
            {
                _modules.Remove(mi);
            }

            _modules.Add(new ModuleInfo(module.ModuleType, typeof(TOther), module));
        }

        public IReadOnlyCollection<ModuleInfo> GetEnabledModules()
        {
            return _modules;
        }

        public IReadOnlyCollection<ModuleInfo> GetAllModules()
        {
            return _modules;
        }
    }
}