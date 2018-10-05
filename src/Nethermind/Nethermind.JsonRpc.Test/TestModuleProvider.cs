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
using Nethermind.JsonRpc.DataModel;
using Nethermind.JsonRpc.Module;
using NSubstitute;

namespace Nethermind.JsonRpc.Test
{
    internal class TestModuleProvider<T> : IModuleProvider where T : class, IModule
    {
        private ModuleInfo[] _modules;

        public TestModuleProvider(T module)
        {
            _modules = new[]
            {
                new ModuleInfo(ModuleType.Net, typeof(INetModule), typeof(INetModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Eth, typeof(IEthModule), typeof(IEthModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Web3, typeof(IWeb3Module), typeof(IWeb3Module).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Shh, typeof(IShhModule), typeof(ShhModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Nethm, typeof(INethmModule), typeof(INethmModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
                new ModuleInfo(ModuleType.Debug, typeof(IDebugModule), typeof(IDebugModule).IsAssignableFrom(typeof(T)) ? module : Substitute.For<T>()),
            };
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