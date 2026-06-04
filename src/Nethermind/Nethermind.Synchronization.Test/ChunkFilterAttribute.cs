// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: Nethermind.Synchronization.Test.ChunkFilterAttribute]

namespace Nethermind.Synchronization.Test;

/// <summary>
/// Assembly-scoped <see cref="ITestAction"/> that partitions tests by stable
/// name hash when <c>TEST_CHUNK</c> is set (e.g. <c>"1of4"</c>). No-op otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ChunkFilterAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
        if (test.IsSuite) return;
        if (!TestChunkFilter.ShouldRunInChunk(test.FullName))
        {
            Assert.Ignore("chunk filter: skipped by TEST_CHUNK partition");
        }
    }

    public void AfterTest(ITest test)
    {
    }
}
