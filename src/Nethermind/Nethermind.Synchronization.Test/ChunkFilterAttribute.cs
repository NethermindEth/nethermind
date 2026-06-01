// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Ethereum.Test.Base;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

// Apply the chunk filter at assembly scope so every [Test]/[TestCase] in
// Nethermind.Synchronization.Test is partitioned when TEST_CHUNK is set.
// When TEST_CHUNK is unset the attribute is a no-op (current behavior).
[assembly: Nethermind.Synchronization.Test.ChunkFilterAttribute]

namespace Nethermind.Synchronization.Test;

/// <summary>
/// NUnit assembly-level action that partitions standalone test methods into
/// chunks for parallel CI execution. Activated by the <c>TEST_CHUNK</c>
/// environment variable (format <c>"1of4"</c>, <c>"2of4"</c>, ...).
/// </summary>
/// <remarks>
/// Standalone <c>[Test]</c> / <c>[TestCase]</c> methods cannot be partitioned
/// by <see cref="TestChunkFilter.FilterByChunk{T}"/> because there is no
/// <c>IEnumerable</c> source to slice. This action runs in NUnit's BeforeTest
/// pipeline and calls <see cref="Assert.Ignore(string)"/> for every test
/// whose stable hash falls outside the selected chunk.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ChunkFilterAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
        // Only filter individual test cases, not fixtures or the suite.
        if (test.IsSuite)
        {
            return;
        }

        // FullName includes namespace + type + method + parameter values, so
        // it is unique and stable across processes and platforms.
        if (!TestChunkFilter.ShouldRunInChunk(test.FullName))
        {
            Assert.Ignore("chunk filter: skipped by TEST_CHUNK partition");
        }
    }

    public void AfterTest(ITest test)
    {
    }
}
