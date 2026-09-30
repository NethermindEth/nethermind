// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test;

public static class SequenceNUnitConstraintExtensions
{
    extension(Is)
    {
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(T[] expected) => new((ReadOnlyMemory<T>)expected);
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(ReadOnlyMemory<T> expected) => new(expected);
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(IEnumerable<T> expected) => new(expected);

        /// <remarks>Copies <paramref name="expected"/> once, as a constraint cannot hold a span.</remarks>
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(ReadOnlySpan<T> expected) => new((ReadOnlyMemory<T>)expected.ToArray());
    }

    extension(Assert)
    {
        /// <remarks><see cref="Assert.That{TActual}(TActual, IResolveConstraint, NUnitString, string, string)"/> cannot take a ref struct.</remarks>
        public static void That<T>(ReadOnlySpan<T> actual, SequenceEqualConstraint<T> expression, string? message = null,
            [CallerArgumentExpression(nameof(actual))] string actualExpression = "",
            [CallerArgumentExpression(nameof(expression))] string constraintExpression = "")
        {
            if (!expression.Matches(actual))
                Assert.That(actual.ToArray(), expression, message, actualExpression, constraintExpression);
        }
    }
}

/// <summary>Element-wise equality for arrays, <see cref="Memory{T}"/>, <see cref="ReadOnlyMemory{T}"/>, spans and any <see cref="IEnumerable{T}"/>.</summary>
/// <remarks>Compares without copying the actual value; only a mismatch materializes arrays so <see cref="EqualConstraint"/> can report the differing index.</remarks>
public sealed class SequenceEqualConstraint<T> : Constraint
{
    private readonly ReadOnlyMemory<T> _expectedMemory;
    private readonly IEnumerable<T>? _expectedEnumerable;

    internal SequenceEqualConstraint(ReadOnlyMemory<T> expected) => _expectedMemory = expected;

    internal SequenceEqualConstraint(IEnumerable<T> expected)
    {
        if (expected is T[] array) _expectedMemory = array;
        else _expectedEnumerable = expected;
    }

    private T[] ExpectedArray => _expectedEnumerable?.ToArray() ?? _expectedMemory.ToArray();

    public override string Description => new EqualConstraint(ExpectedArray).Description;

    public override ConstraintResult ApplyTo<TActual>(TActual actual)
    {
        bool matches = actual switch
        {
            T[] array => Matches(array),
            ReadOnlyMemory<T> memory => Matches(memory.Span),
            Memory<T> memory => Matches(memory.Span),
            ArraySegment<T> segment => Matches(segment.AsSpan()),
            IEnumerable<T> enumerable => MatchesEnumerable(enumerable),
            _ => false
        };

        return matches
            ? new ConstraintResult(this, actual, true)
            : new EqualConstraint(ExpectedArray).ApplyTo(actual switch
            {
                ReadOnlyMemory<T> memory => memory.ToArray(),
                Memory<T> memory => memory.ToArray(),
                _ => (object?)actual
            });
    }

    internal bool Matches(ReadOnlySpan<T> actual)
    {
        if (_expectedEnumerable is null) return actual.SequenceEqual(_expectedMemory.Span, EqualityComparer<T>.Default);

        int i = 0;
        foreach (T item in _expectedEnumerable)
        {
            if (i >= actual.Length || !EqualityComparer<T>.Default.Equals(actual[i++], item)) return false;
        }

        return i == actual.Length;
    }

    private bool MatchesEnumerable(IEnumerable<T> actual)
    {
        if (_expectedEnumerable is not null) return actual.SequenceEqual(_expectedEnumerable);

        ReadOnlySpan<T> expected = _expectedMemory.Span;
        int i = 0;
        foreach (T item in actual)
        {
            if (i >= expected.Length || !EqualityComparer<T>.Default.Equals(item, expected[i++])) return false;
        }

        return i == expected.Length;
    }
}
