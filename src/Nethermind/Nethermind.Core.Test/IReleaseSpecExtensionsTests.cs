// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class IReleaseSpecExtensionsTests
{
    [Test]
    public void WithoutEip158_returns_the_same_cached_wrapper_per_spec() => AssertWrapperIsCachedPerSpec(
        enable: static (spec, value) => spec.IsEip158Enabled.Returns(value),
        wrap: static spec => spec.WithoutEip158(),
        isEnabled: static spec => spec.IsEip158Enabled);

    [Test]
    public void WithoutEip3607_returns_the_same_cached_wrapper_per_spec() => AssertWrapperIsCachedPerSpec(
        enable: static (spec, value) => spec.IsEip3607Enabled.Returns(value),
        wrap: static spec => spec.WithoutEip3607(),
        isEnabled: static spec => spec.IsEip3607Enabled);

    private static void AssertWrapperIsCachedPerSpec(
        Action<IReleaseSpec, bool> enable,
        Func<IReleaseSpec, IReleaseSpec> wrap,
        Func<IReleaseSpec, bool> isEnabled)
    {
        IReleaseSpec disabledSpec = ReleaseSpecSubstitute.Create();
        enable(disabledSpec, false);
        Assert.That(wrap(disabledSpec), Is.SameAs(disabledSpec));

        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        enable(spec, true);
        IReleaseSpec wrapper = wrap(spec);
        Assert.That(wrapper, Is.Not.SameAs(spec));
        Assert.That(isEnabled(wrapper), Is.False);
        Assert.That(wrap(spec), Is.SameAs(wrapper));
        Assert.That(wrap(wrapper), Is.SameAs(wrapper));

        IReleaseSpec otherSpec = ReleaseSpecSubstitute.Create();
        enable(otherSpec, true);
        Assert.That(wrap(otherSpec), Is.Not.SameAs(wrapper));

        enable(spec, false);
        Assert.That(wrap(spec), Is.SameAs(spec));
    }
}
