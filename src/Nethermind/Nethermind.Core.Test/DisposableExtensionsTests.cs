// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class DisposableExtensionsTests
{
    [Test]
    public void TryDispose_calls_Dispose_on_IDisposable()
    {
        IDisposable disposable = Substitute.For<IDisposable>();

        disposable.TryDispose();

        disposable.Received(1).Dispose();
    }

    [Test]
    public void TryDispose_does_nothing_on_non_disposable()
    {
        object obj = new();

        // Should not throw
        obj.TryDispose();
    }

    [Test]
    public void TryDispose_disposes_disposable_element_and_skips_non_disposable_elements_inside_tuple()
    {
        IDisposable inner = Substitute.For<IDisposable>();
        (IDisposable, long) tuple = (inner, 42L);

        tuple.TryDispose();

        inner.Received(1).Dispose();
    }

    [Test]
    public void TryDispose_disposes_multiple_disposable_elements_inside_tuple()
    {
        IDisposable first = Substitute.For<IDisposable>();
        IDisposable second = Substitute.For<IDisposable>();
        (IDisposable, IDisposable) tuple = (first, second);

        tuple.TryDispose();

        first.Received(1).Dispose();
        second.Received(1).Dispose();
    }
}
