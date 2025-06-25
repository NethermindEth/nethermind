// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Analytics;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.PubSub;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json.PubSub;

namespace Nethermind.Analytics
{
    public class AnalyticsPlugin(IInitConfig initConfig, IAnalyticsConfig analyticsConfig) : INethermindPlugin
    {
        public bool Enabled => initConfig.WebSocketsEnabled &&
                               (analyticsConfig.PluginsEnabled ||
                                analyticsConfig.StreamBlocks ||
                                analyticsConfig.StreamTransactions);

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

        public string Name => "Analytics";

        public string Description => "Various Analytics Extensions";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api) => Task.CompletedTask;

        public Task InitNetworkProtocol() => Task.CompletedTask;

        public Task InitRpcModules() => Task.CompletedTask;

        public IModule Module => new AnalyticsModule();
    }
}

public class AnalyticsModule: Module
{
    protected override void Load(ContainerBuilder builder) => builder

        // Standard IPublishers, which seems to be things that publish when txpool got transaction
        .AddSingleton<AnalyticsWebSocketsModule>()
            .Bind<IPublisher, AnalyticsWebSocketsModule>() // send to websocket
        .AddSingleton<IPublisher, LogPublisher>() // Send to log

        // Rpc
        .RegisterSingletonJsonRpcModule<IAnalyticsRpcModule, AnalyticsRpcModule>()

        // Step
        .AddStep(typeof(AnalyticsSteps))
    ;
}
