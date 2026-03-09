// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics.X86;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Nethermind.Core.Test;

/// <summary>
/// Skips the test when the specified hardware intrinsic is not supported on the current platform.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequiresIntrinsicAttribute(string intrinsicName) : NUnitAttribute, IApplyToTest
{
    public void ApplyToTest(NUnit.Framework.Internal.Test test)
    {
        bool supported = intrinsicName switch
        {
            nameof(Sse41) => Sse41.IsSupported,
            nameof(Avx2) => Avx2.IsSupported,
            _ => throw new ArgumentException($"Unknown intrinsic: {intrinsicName}")
        };

        if (!supported)
        {
            test.RunState = RunState.Skipped;
            test.Properties.Set(NUnit.Framework.Internal.PropertyNames.SkipReason, $"{intrinsicName} is not supported on this platform");
        }
    }
}
