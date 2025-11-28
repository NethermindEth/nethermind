// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class InitializePluginsTests
{
    [Test]
    public async Task Registers_disposable_plugins_for_shutdown()
    {
        DisposablePlugin plugin = new();
        IDisposableStack disposeStack = Substitute.For<IDisposableStack>();
        INethermindApi api = CreateApi(disposeStack, new List<INethermindPlugin> { plugin });

        InitializePlugins step = new(api);
        await step.Execute(CancellationToken.None);

        disposeStack.Received(1).Push((IDisposable)plugin);
    }

    [Test]
    public async Task Registers_async_disposable_plugins_for_shutdown()
    {
        AsyncDisposablePlugin plugin = new();
        IDisposableStack disposeStack = Substitute.For<IDisposableStack>();
        INethermindApi api = CreateApi(disposeStack, new List<INethermindPlugin> { plugin });

        InitializePlugins step = new(api);
        await step.Execute(CancellationToken.None);

        disposeStack.Received(1).Push((IAsyncDisposable)plugin);
        disposeStack.DidNotReceive().Push(Arg.Any<IDisposable>());
    }

    private static INethermindApi CreateApi(IDisposableStack disposeStack, IReadOnlyList<INethermindPlugin> plugins)
    {
        INethermindApi api = Substitute.For<INethermindApi>();
        ILogManager logManager = LimboLogs.Instance;
        api.LogManager.Returns(logManager);
        api.Plugins.Returns(plugins);
        api.DisposeStack.Returns(disposeStack);
        return api;
    }

    private abstract class TestPluginBase : INethermindPlugin
    {
        public string Name => GetType().Name;
        public string Description => string.Empty;
        public string Author => "test";
        public bool Enabled => true;
        public Task Init(INethermindApi nethermindApi) => Task.CompletedTask;
    }

    private sealed class DisposablePlugin : TestPluginBase, IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class AsyncDisposablePlugin : TestPluginBase, IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
