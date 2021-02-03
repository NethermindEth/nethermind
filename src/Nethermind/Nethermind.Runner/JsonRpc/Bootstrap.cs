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

using Microsoft.Extensions.DependencyInjection;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.JsonRpc
{
    public class Bootstrap
    {
        private static Bootstrap? _instance;

        private Bootstrap() { }

        public static Bootstrap Instance => _instance ??= new Bootstrap();

        public IJsonRpcService? JsonRpcService { private get; set; }
        public ILogManager? LogManager { private get; set; }
        public IJsonSerializer? JsonSerializer { private get; set; }
        public IJsonRpcLocalStats? JsonRpcLocalStats { private get; set; }

        public void RegisterJsonRpcServices(IServiceCollection services)
        {
            services.AddSingleton(JsonRpcService!);
            services.AddSingleton(LogManager!);
            services.AddSingleton(JsonSerializer!);
            services.AddSingleton(JsonRpcLocalStats!);
        }
    }
}
