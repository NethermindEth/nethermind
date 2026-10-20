// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework.Interfaces;

namespace Nethermind.Core.Test;

/// <summary>
/// Assembly-scoped attribute that partitions tests by stable
/// name hash when <c>TEST_CHUNK</c> is set (e.g. <c>"1of4"</c>). No-op otherwise.
/// </summary>
/// <remarks>
/// Tests outside the chunk are removed from the test tree when it is built, so they are neither run nor reported;
/// ignoring them at run time printed each one as skipped.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ChunkFilterAttribute : Attribute, IApplyToTest
{
    public void ApplyToTest(NUnit.Framework.Internal.Test test)
    {
        if (TestChunkFilter.TryGetChunkConfig() is not null)
        {
            RemoveTestsOutsideChunk(test);
        }
    }

    /// <returns>Whether removing tests emptied <paramref name="suite"/>.</returns>
    /// <remarks>A suite built without tests is kept, so it still reports why it has none.</remarks>
    private static bool RemoveTestsOutsideChunk(ITest suite)
    {
        IList<ITest> tests = suite.Tests;
        if (tests.Count == 0)
        {
            return false;
        }

        for (int i = tests.Count - 1; i >= 0; i--)
        {
            ITest test = tests[i];
            if (test.IsSuite ? RemoveTestsOutsideChunk(test) : !TestChunkFilter.ShouldRunInChunk(test.FullName))
            {
                tests.RemoveAt(i);
            }
        }

        return tests.Count == 0;
    }
}
