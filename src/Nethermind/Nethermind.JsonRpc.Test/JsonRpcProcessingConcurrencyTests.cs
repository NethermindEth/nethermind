// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.All)]
public class JsonRpcProcessingConcurrencyTests
{
    private const int Configured = 16;

    [TestCase(true, true, 1, TestName = "ForUrl_AuthenticatedEngineUrl_ProcessesOneAtATime")]
    [TestCase(true, false, 1, TestName = "ForUrl_AuthenticatedUrlWithoutEngine_ProcessesOneAtATime")]
    [TestCase(false, true, 1, TestName = "ForUrl_EngineUrlWithoutAuth_ProcessesOneAtATime")]
    [TestCase(false, false, Configured, TestName = "ForUrl_PublicUrl_UsesTheConfiguredConcurrency")]
    public void ForUrl_FollowsTheUrl(bool isAuthenticated, bool engine, int expected)
    {
        JsonRpcUrl url = new("http", "127.0.0.1", 8551, RpcEndpoint.Http | RpcEndpoint.Ws, isAuthenticated,
            engine ? [ModuleType.Engine, ModuleType.Eth] : [ModuleType.Eth]);

        Assert.That(JsonRpcProcessingConcurrency.ForUrl(url, Configured), Is.EqualTo(expected),
            "the engine API needs its pipelined requests run in order; only public connections run them concurrently");
    }

    [TestCase(new[] { ModuleType.Engine, ModuleType.Eth }, 1, TestName = "ForModules_EngineEnabled_ProcessesOneAtATime")]
    [TestCase(new[] { "engine", ModuleType.Eth }, 1, TestName = "ForModules_EngineEnabledInLowerCase_ProcessesOneAtATime")]
    [TestCase(new[] { ModuleType.Eth, ModuleType.Net }, Configured, TestName = "ForModules_EngineDisabled_UsesTheConfiguredConcurrency")]
    public void ForModules_FollowsTheEnabledModules(string[] enabledModules, int expected) =>
        Assert.That(JsonRpcProcessingConcurrency.ForModules(enabledModules, Configured), Is.EqualTo(expected),
            "the engine API needs ordered requests whenever it is enabled for the connection");
}
