// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class RpcModulesTests
{
    [Test]
    public void TracingModuleConcurrentInstances_WithDefaultConfig_TraceMatchesDebug()
    {
        JsonRpcConfig config = new();
        int expected = Math.Min(Environment.ProcessorCount, 16);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RpcModules.TracingModuleConcurrentInstances(config.TraceModuleConcurrentInstances), Is.EqualTo(expected),
                "an unset trace instance count resolves to the logical processors capped at 16");
            Assert.That(RpcModules.TracingModuleConcurrentInstances(config.DebugModuleConcurrentInstances), Is.EqualTo(expected),
                "the debug module resolves to the same default");
        }
    }

    [TestCase(1, TestName = "TracingModuleConcurrentInstances_WhenConfiguredToOne_KeepsOne")]
    [TestCase(40, TestName = "TracingModuleConcurrentInstances_WhenConfiguredAboveTheCap_KeepsTheConfiguredValue")]
    public void TracingModuleConcurrentInstances_WhenConfigured_UsesTheConfiguredValue(int configured) =>
        Assert.That(RpcModules.TracingModuleConcurrentInstances(configured), Is.EqualTo(configured),
            "an explicit instance count is never replaced by the default");
}
