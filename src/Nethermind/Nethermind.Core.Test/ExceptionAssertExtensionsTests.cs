// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class ExceptionAssertExtensionsTests
{
    [Test]
    public void NotThrow_passes_when_no_exception()
    {
        Action action = static () => { };

        Assert.DoesNotThrow<InvalidOperationException>(action);
    }

    [Test]
    public void NotThrow_passes_when_different_exception_is_thrown()
    {
        Action action = static () => throw new ArgumentException();

        Assert.DoesNotThrow<InvalidOperationException>(action);
    }

    [Test]
    public void NotThrow_fails_when_matching_exception_is_thrown()
    {
        Action action = static () => throw new InvalidOperationException();

        Assert.That(() => Assert.DoesNotThrow<InvalidOperationException>(action), Throws.TypeOf<AssertionException>());
    }

    [Test]
    public void NotThrow_fails_when_derived_exception_is_thrown()
    {
        Action action = static () => throw new TaskCanceledException();

        Assert.That(() => Assert.DoesNotThrow<OperationCanceledException>(action), Throws.TypeOf<AssertionException>());
    }
}
