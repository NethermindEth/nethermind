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
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(T[] expected) => new(expected);
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(ReadOnlyMemory<T> expected) => new(expected.ToArray());
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(Memory<T> expected) => new(expected.ToArray());
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(IEnumerable<T> expected) => new(expected);

        /// <remarks>Copies <paramref name="expected"/> once, as a constraint cannot hold a span.</remarks>
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(ReadOnlySpan<T> expected) => new(expected.ToArray());
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
/// <remarks>Compares an array, memory or span of primitives or enums in place; a mismatch, or any other element type, goes to <see cref="EqualConstraint"/>, which reports the differing index.</remarks>
public sealed class SequenceEqualConstraint<T> : Constraint
{
    // NUnit may compare other types structurally rather than by Equals, e.g. nested sequences or IStructuralEquatable.
    private static readonly bool ElementsCompareByEquals = typeof(T).IsPrimitive || typeof(T).IsEnum;

    private readonly T[] _expected;

    /// <remarks>Enumerates a lazy <paramref name="expected"/> once; null throws rather than matching an empty actual.</remarks>
    internal SequenceEqualConstraint(IEnumerable<T> expected) => _expected = expected as T[] ?? expected.ToArray();

    public override string Description => new EqualConstraint(_expected).Description;

    public override ConstraintResult ApplyTo<TActual>(TActual actual)
    {
        // A default segment has no contents to compare, though AsSpan reads it as empty.
        if (actual is ArraySegment<T> { Array: null }) return new ConstraintResult(this, actual, false);

        bool matches = actual switch
        {
            T[] array => Matches(array),
            ReadOnlyMemory<T> memory => Matches(memory.Span),
            Memory<T> memory => Matches(memory.Span),
            ArraySegment<T> segment => Matches(segment.AsSpan()),
            _ => false
        };

        return matches
            ? new ConstraintResult(this, actual, true)
            : new EqualConstraint(_expected).ApplyTo(actual switch
            {
                ReadOnlyMemory<T> memory => memory.ToArray(),
                Memory<T> memory => memory.ToArray(),
                _ => (object?)actual
            });
    }

    internal bool Matches(ReadOnlySpan<T> actual) => ElementsCompareByEquals && actual.SequenceEqual(_expected, EqualityComparer<T>.Default);
}
