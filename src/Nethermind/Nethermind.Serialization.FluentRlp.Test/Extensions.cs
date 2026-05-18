// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using FluentAssertions.Collections;

namespace Nethermind.Serialization.FluentRlp.Test;

// NOTE: `FluentAssertions` currently does not support `(ReadOnly)Span<T>` or `(ReadOnly)Memory<T>` assertions.
public static class Extensions
{
    public static GenericCollectionAssertions<T> Should<T>(this ReadOnlySpan<T> span) => span.ToArray().Should();
    public static GenericCollectionAssertions<T> Should<T>(this ReadOnlyMemory<T> memory) => memory.ToArray().Should();

    public static AndConstraint<GenericCollectionAssertions<TExpectation>> BeEquivalentTo<TExpectation>(
        this GenericCollectionAssertions<TExpectation> @this,
        ReadOnlySpan<TExpectation> expectation,
        string because = "",
        params object[] becauseArgs)
    {
        return @this.BeEquivalentTo(expectation.ToArray(), config => config, because, becauseArgs);
    }

    public static AndConstraint<GenericCollectionAssertions<TExpectation>> BeEquivalentTo<TExpectation>(
        this GenericCollectionAssertions<TExpectation> @this,
        ReadOnlyMemory<TExpectation> expectation,
        string because = "",
        params object[] becauseArgs)
    {
        return @this.BeEquivalentTo(expectation.ToArray(), config => config, because, becauseArgs);
    }
}
