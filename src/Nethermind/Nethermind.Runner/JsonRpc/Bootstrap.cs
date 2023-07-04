// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Authentication;
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
        public IRpcAuthentication? JsonRpcAuthentication { private get; set; }

        public void RegisterJsonRpcServices(IServiceCollection services)
        {
            services.AddSingleton(JsonRpcService!);
            services.AddSingleton(LogManager!);
            services.AddSingleton(JsonSerializer!);
            services.AddSingleton(JsonRpcLocalStats!);
            services.AddSingleton(JsonRpcAuthentication!);
        }
    }
}
