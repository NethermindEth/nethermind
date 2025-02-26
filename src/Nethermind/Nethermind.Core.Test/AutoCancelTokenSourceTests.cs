// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class AutoCancelTokenSourceTests
{
    [Test]
    public void AutoCancelOnExitClosure()
    {
        CancellationToken TaskWithInnerCancellation(CancellationToken token)
        {
            using AutoCancelTokenSource cts = token.CreateChildTokenSource();
            return cts.Token;
        }

        TaskWithInnerCancellation(default).IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void AutoCancelPropagateParentCancellation()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();

        using AutoCancelTokenSource acts = cts.Token.CreateChildTokenSource();

        acts.Token.IsCancellationRequested.Should().BeFalse();

        cts.Cancel();

        acts.Token.IsCancellationRequested.Should().BeTrue();
    }
}
