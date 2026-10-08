// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>Fails a test that leaves a <see cref="BaseEngineModuleTests.MergeTestBlockchain"/> it built undisposed.</summary>
/// <remarks>
/// A merge test chain stays reachable through its timers and background loops, so one that is never disposed is
/// never collected; a few dozen of them exhaust a CI runner's memory. Only chains built while a guarded test runs
/// are tracked, so assemblies without the attribute keep no references.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class UndisposedChainGuardAttribute : Attribute, ITestAction
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<BaseEngineModuleTests.MergeTestBlockchain>> BuiltByTest = new();

    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test) => BuiltByTest[test.Id] = new();

    public void AfterTest(ITest test)
    {
        if (!BuiltByTest.TryRemove(test.Id, out ConcurrentQueue<BaseEngineModuleTests.MergeTestBlockchain>? chains)) return;

        int undisposed = 0;
        foreach (BaseEngineModuleTests.MergeTestBlockchain chain in chains)
        {
            if (!chain.IsDisposed) undisposed++;
        }

        if (undisposed > 0)
        {
            Assert.Fail($"{test.FullName} left {undisposed} {nameof(BaseEngineModuleTests.MergeTestBlockchain)} undisposed; wrap it in a using.");
        }
    }

    internal static void Track(BaseEngineModuleTests.MergeTestBlockchain chain)
    {
        if (BuiltByTest.TryGetValue(TestContext.CurrentContext.Test.ID, out ConcurrentQueue<BaseEngineModuleTests.MergeTestBlockchain>? chains))
        {
            chains.Enqueue(chain);
        }
    }
}
