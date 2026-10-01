// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Core.Test;

public static class SequenceNUnitConstraintExtensions
{
    extension(Is)
    {
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(T[] expected) => new((ReadOnlyMemory<T>)(expected ?? throw new ArgumentNullException(nameof(expected))));
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(ReadOnlyMemory<T> expected) => new(expected);
        public static SequenceEqualConstraint<T> SequenceEqualTo<T>(Memory<T> expected) => new((ReadOnlyMemory<T>)expected);
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
/// <remarks>
/// Compares sequences of primitives or enums in place, reading a lazy sequence once while comparing, so a pass copies neither
/// side unless both are lazy. A mismatch, or any other element type, goes to <see cref="EqualConstraint"/>, which reports the
/// differing index; a lazy side is then rebuilt from the prefix that matched rather than read again.
/// </remarks>
public sealed class SequenceEqualConstraint<T> : Constraint
{
    // NUnit may compare other types structurally rather than by Equals, e.g. nested sequences or IStructuralEquatable.
    private static readonly bool ElementsCompareByEquals = typeof(T).IsPrimitive || typeof(T).IsEnum;

    private readonly ReadOnlyMemory<T> _expectedMemory;
    private readonly IEnumerable<T>? _expectedSequence;
    private T[]? _expectedRead;

    internal SequenceEqualConstraint(ReadOnlyMemory<T> expected) => _expectedMemory = expected;

    /// <remarks>Null throws rather than matching an empty actual.</remarks>
    internal SequenceEqualConstraint(IEnumerable<T> expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected is T[] array) _expectedMemory = array;
        else _expectedSequence = expected;
    }

    public override string Description => new EqualConstraint(ExpectedArray).Description;

    private T[] ExpectedArray => _expectedRead ??= _expectedSequence switch
    {
        null when MemoryMarshal.TryGetArray(_expectedMemory, out ArraySegment<T> segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length => segment.Array,
        null => _expectedMemory.ToArray(),
        var sequence => sequence.ToArray()
    };

    private bool TryGetExpectedSpan(out ReadOnlySpan<T> expected)
    {
        expected = _expectedRead ?? _expectedSequence switch
        {
            null => _expectedMemory.Span,
            List<T> list => CollectionsMarshal.AsSpan(list),
            _ => default
        };
        return _expectedRead is not null || _expectedSequence is null or List<T>;
    }

    public override ConstraintResult ApplyTo<TActual>(TActual actual)
    {
        // A default segment has no contents to compare, though AsSpan reads it as empty.
        if (actual is ArraySegment<T> { Array: null }) return new ConstraintResult(this, actual, false);

        T[]? actualRead = null;
        bool matches = ElementsCompareByEquals && actual switch
        {
            T[] array => Matches(array),
            ReadOnlyMemory<T> memory => Matches(memory.Span),
            Memory<T> memory => Matches(memory.Span),
            ArraySegment<T> segment => Matches(segment.AsSpan()),
            List<T> list => Matches(CollectionsMarshal.AsSpan(list)),
            IEnumerable<T> sequence => Matches(sequence, out actualRead),
            _ => false
        };

        return matches
            ? new ConstraintResult(this, actual, true)
            : new EqualConstraint(ExpectedArray).ApplyTo(actual switch
            {
                T[] array => array,
                ReadOnlyMemory<T> memory => memory.ToArray(),
                Memory<T> memory => memory.ToArray(),
                IEnumerable<T> sequence => actualRead ?? sequence.ToArray(),
                _ => (object?)actual
            });
    }

    internal bool Matches(ReadOnlySpan<T> actual)
    {
        if (!ElementsCompareByEquals) return false;
        if (TryGetExpectedSpan(out ReadOnlySpan<T> expected)) return actual.SequenceEqual(expected, EqualityComparer<T>.Default);

        using IEnumerator<T> enumerator = _expectedSequence!.GetEnumerator();
        bool matches = MatchesPrefix(enumerator, actual, out int read, out bool stoppedOnElement);
        if (!matches) _expectedRead = Rebuild(actual[..read], enumerator, stoppedOnElement);
        return matches;
    }

    private bool Matches(IEnumerable<T> actual, out T[]? actualRead)
    {
        if (!TryGetExpectedSpan(out ReadOnlySpan<T> expected)) expected = ExpectedArray;

        using IEnumerator<T> enumerator = actual.GetEnumerator();
        bool matches = MatchesPrefix(enumerator, expected, out int read, out bool stoppedOnElement);
        actualRead = matches ? null : Rebuild(expected[..read], enumerator, stoppedOnElement);
        return matches;
    }

    /// <summary>Reads <paramref name="sequence"/> against <paramref name="other"/> until they differ or both end.</summary>
    /// <param name="read">Elements of <paramref name="sequence"/> read so far that equal the start of <paramref name="other"/>.</param>
    /// <param name="stoppedOnElement">Whether a mismatch left <paramref name="sequence"/> on an element it read, rather than past its end.</param>
    private static bool MatchesPrefix(IEnumerator<T> sequence, ReadOnlySpan<T> other, out int read, out bool stoppedOnElement)
    {
        for (read = 0; sequence.MoveNext(); read++)
        {
            if (read >= other.Length || !EqualityComparer<T>.Default.Equals(sequence.Current, other[read]))
            {
                stoppedOnElement = true;
                return false;
            }
        }

        stoppedOnElement = false;
        return read == other.Length;
    }

    // The elements already read equal matchedPrefix, so the sequence is rebuilt without being read again.
    private static T[] Rebuild(ReadOnlySpan<T> matchedPrefix, IEnumerator<T> rest, bool stoppedOnElement)
    {
        List<T> elements = [.. matchedPrefix];
        if (stoppedOnElement)
        {
            elements.Add(rest.Current);
            while (rest.MoveNext()) elements.Add(rest.Current);
        }

        return [.. elements];
    }
}
