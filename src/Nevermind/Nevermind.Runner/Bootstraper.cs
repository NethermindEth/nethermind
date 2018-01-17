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

using Nevermind.Core;
using Nevermind.Json;
using Nevermind.JsonRpc;
using Nevermind.JsonRpc.Module;
using Unity;

namespace Nevermind.Runner
{
    public class Bootstraper
    {
        public IUnityContainer Container { get; set; }

        public Bootstraper()
        {
            Container = new UnityContainer();
            ConfigureContainer();
        }

        private void ConfigureContainer()
        {
            Container.RegisterType<ILogger, ConsoleLogger>();
            Container.RegisterType<IConfigurationProvider, ConfigurationProvider>();
            Container.RegisterType<IJsonSerializer, JsonSerializer>();
            Container.RegisterType<INetModule, NetModule>();
            Container.RegisterType<IWeb3Module, Web3Module>();
            Container.RegisterType<IEthModule, EthModule>();
            Container.RegisterType<IJsonRpcService, JsonRpcService>();

            Container.RegisterType<IJsonRpcRunner, JsonRpcRunner>();
            Container.RegisterType<IEthereumRunner, EthereumRunner>();
        }
    }
}