// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Checks that a test run pinned to an instruction set got the one it asked for.
/// </summary>
/// <remarks>
/// A vector instruction set is fixed for the life of a process, so the halves of an
/// <c>IsSupported</c> branch can only be covered by separate runs. The extra test variants pin one
/// and set the matching variable; without this check a run whose pin failed to reach the runtime
/// would pass while covering nothing, which is indistinguishable from covering everything.
/// </remarks>
public static class VectorIsaExpectations
{
    /// <summary>Fails the run when the pinned instruction set is not the one in force.</summary>
    public static void AssertPinnedInstructionSet()
    {
        TestContext.Progress.WriteLine($"AVX2={Avx2.IsSupported}; AVX512F={Avx512F.IsSupported}; AVX512VBMI={Avx512Vbmi.IsSupported}");
        if (Environment.GetEnvironmentVariable("NETHERMIND_TEST_REQUIRE_AVX512") == "1")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Avx512F.IsSupported, Is.True, "The AVX-512 job requires AVX-512F hardware.");
                Assert.That(Avx512F.VL.IsSupported, Is.True, "The AVX-512 job requires AVX-512VL hardware.");
            }
        }

        if (Environment.GetEnvironmentVariable("NETHERMIND_TEST_REQUIRE_AVX512VBMI") == "1")
        {
            Assert.That(Avx512Vbmi.IsSupported && Vector512.IsHardwareAccelerated, Is.True,
                "The AVX-512 job requires AVX-512 VBMI with 512-bit vectors enabled.");
        }

        if (Environment.GetEnvironmentVariable("NETHERMIND_TEST_REQUIRE_AVX2") == "1")
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Avx2.IsSupported, Is.True, "The AVX2 job requires AVX2 hardware.");
                Assert.That(Avx512F.IsSupported, Is.False, "The AVX2 job must disable AVX-512.");
            }
        }
    }
}
