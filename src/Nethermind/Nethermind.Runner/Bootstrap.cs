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

using System;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Module;

namespace Nethermind.Runner
{
    public class Bootstrap
    {
        private static Bootstrap _instance;

        private Bootstrap()
        {
        }

        public static Bootstrap Instance => _instance ?? (_instance = new Bootstrap());

        public IConfigProvider ConfigProvider { private get; set; }
        public ILogManager LogManager { private get; set; }
        public IBlockchainBridge BlockchainBridge { private get; set; }
        public IDebugBridge DebugBridge { private get; set; }
        public INetBridge NetBridge { private get; set; }

        public void RegisterJsonRpcServices(IServiceCollection services)
        {
            if (ConfigProvider == null)
            {
                throw new Exception("ConfigProvider is required");
            }
            if (LogManager == null)
            {
                throw new Exception("LogManager is required");
            }
            if (BlockchainBridge == null)
            {
                throw new Exception("BlockchainBridge is required");
            }

            //JsonRPC            
            services.AddSingleton<IConfigProvider>(ConfigProvider);
            services.AddSingleton<ILogManager>(LogManager);
            services.AddSingleton<IBlockchainBridge>(BlockchainBridge);
            services.AddSingleton<IDebugBridge>(DebugBridge);
            services.AddSingleton<INetBridge>(NetBridge);
            services.AddSingleton<IJsonSerializer, JsonSerializer>();
            services.AddSingleton<IJsonRpcModelMapper, JsonRpcModelMapper>();
            services.AddSingleton<IModuleProvider, ModuleProvider>();
            services.AddSingleton<INetModule, NetModule>();
            services.AddSingleton<IWeb3Module, Web3Module>();
            services.AddSingleton<IEthModule, EthModule>();
            services.AddSingleton<IShhModule, ShhModule>();
            services.AddSingleton<INethmModule, NethmModule>();
            services.AddSingleton<IDebugModule, DebugModule>();
            services.AddSingleton<IJsonRpcService, JsonRpcService>();
        }
    }
}