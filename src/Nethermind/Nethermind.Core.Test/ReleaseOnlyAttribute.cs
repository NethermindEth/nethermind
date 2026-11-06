// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnitTest = NUnit.Framework.Internal.Test;

namespace Nethermind.Core.Test;

/// <summary>
/// Ignores the test or fixture in builds that define <c>DEBUG</c>.
/// </summary>
/// <remarks>
/// For tests whose expectations only hold without the <c>#if DEBUG</c> code paths, such as allocation ceilings.
/// The checked CI variant is a Release build that defines <c>DEBUG</c>, so it skips these tests too.
/// </remarks>
/// <param name="reason">Why the test does not apply to <c>DEBUG</c> builds; reported as the skip reason.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ReleaseOnlyAttribute(string reason) : NUnitAttribute, IApplyToTest
{
    public string Reason { get; } = reason;

    public void ApplyToTest(NUnitTest test)
    {
#if DEBUG
        if (test.RunState is RunState.NotRunnable) return;
        test.RunState = RunState.Ignored;
        test.Properties.Set(NUnit.Framework.Internal.PropertyNames.SkipReason, Reason);
#endif
    }
}
