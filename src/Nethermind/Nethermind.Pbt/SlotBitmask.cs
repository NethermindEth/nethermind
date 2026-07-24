// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>
/// A set of one tile's boundary slots: bit <c>i</c> is set where slot <c>i</c> is in it.
/// </summary>
/// <remarks>
/// Sized for the widest tiling (<see cref="PbtLayout.MaxBoundarySlots"/>) and read only as far as
/// <typeparamref name="TLayout"/> reaches, so a tiling whose boundary fits one word folds back to the
/// single-word arithmetic this replaced — <see cref="IPbtTileLayout.BoundaryMaskWordCount"/> being a
/// constant of the type parameter rather than a value read at runtime.
/// <para>
/// Held by value where <see cref="PbtBitset"/> operates on caller-owned spans: the fold passes a
/// group's shape between frames and up out of them, which a span into one frame's stack cannot
/// outlive.
/// </para>
/// </remarks>
internal struct SlotBitmask<TLayout> : IEquatable<SlotBitmask<TLayout>> where TLayout : IPbtTileLayout
{
    private const int WordCapacity = PbtLayout.MaxBoundarySlots / PbtBitset.BitsPerWord;

    [InlineArray(WordCapacity)]
    private struct WordArray
    {
        private ulong _element0;
    }

    private WordArray _words;

    /// <summary>The set holding exactly <paramref name="slots"/>.</summary>
    public static SlotBitmask<TLayout> Of(params ReadOnlySpan<int> slots)
    {
        SlotBitmask<TLayout> mask = default;
        foreach (int slot in slots) mask.Set(slot);
        return mask;
    }

    /// <summary>
    /// The words backing this set, as far as the layout reaches, for a reader or writer that works in
    /// spans.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>readonly</c>: a readonly getter would defensively copy the inline array and
    /// hand back a span over the throwaway copy, so a caller writing through it would write to nothing.
    /// </remarks>
    [UnscopedRef]
    public Span<ulong> Words() => ((Span<ulong>)_words)[..TLayout.BoundaryMaskWordCount];

    /// <inheritdoc cref="Words"/>
    [UnscopedRef]
    private readonly ReadOnlySpan<ulong> ReadOnlyWords => ((ReadOnlySpan<ulong>)_words)[..TLayout.BoundaryMaskWordCount];

    public readonly bool this[int slot]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert((uint)slot < (uint)TLayout.BoundarySlots);
            return PbtBitset.Contains(ReadOnlyWords, slot);
        }
    }

    public readonly bool IsEmpty => !PbtBitset.Any(ReadOnlyWords);

    /// <summary>The number of slots in the set.</summary>
    public readonly int Count => PbtBitset.PopCount(ReadOnlyWords);

    /// <summary>Whether the set holds exactly one slot, which <see cref="First"/> then names.</summary>
    public readonly bool HoldsOne => PbtBitset.IsSingleBit(ReadOnlyWords);

    /// <summary>The lowest slot in the set, or <c>-1</c> when it is empty.</summary>
    public readonly int First => PbtBitset.FirstSet(ReadOnlyWords);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int slot)
    {
        Debug.Assert((uint)slot < (uint)TLayout.BoundarySlots);
        PbtBitset.Set(Words(), slot);
    }

    /// <summary>The number of slots in the set below <paramref name="slot"/>.</summary>
    public readonly int CountBelow(int slot) => PbtBitset.PopCountRange(ReadOnlyWords, 0, slot);

    /// <summary>The number of slots the set holds in <c>[firstSlot, firstSlot + width)</c>.</summary>
    public readonly int CountInRange(int firstSlot, int width) => PbtBitset.PopCountRange(ReadOnlyWords, firstSlot, width);

    /// <summary>Whether the set holds any slot in <c>[firstSlot, firstSlot + width)</c>.</summary>
    public readonly bool AnyInRange(int firstSlot, int width) => PbtBitset.AnyInRange(ReadOnlyWords, firstSlot, width);

    /// <summary>The lowest slot the set holds in <c>[firstSlot, firstSlot + width)</c>, or <c>-1</c> when it holds none.</summary>
    public readonly int FirstInRange(int firstSlot, int width) => PbtBitset.FirstSetInRange(ReadOnlyWords, firstSlot, width);

    public static SlotBitmask<TLayout> operator |(SlotBitmask<TLayout> left, SlotBitmask<TLayout> right)
    {
        PbtBitset.Or(left.Words(), right.ReadOnlyWords);
        return left;
    }

    public static SlotBitmask<TLayout> operator &(SlotBitmask<TLayout> left, SlotBitmask<TLayout> right)
    {
        PbtBitset.And(left.Words(), right.ReadOnlyWords);
        return left;
    }

    /// <summary>The slots this set holds and <paramref name="excluded"/> does not.</summary>
    public readonly SlotBitmask<TLayout> Except(SlotBitmask<TLayout> excluded)
    {
        SlotBitmask<TLayout> remaining = this;
        PbtBitset.AndNot(remaining.Words(), excluded.ReadOnlyWords);
        return remaining;
    }

    /// <summary>The set's slots in ascending order, which is the order every fold walks a boundary in.</summary>
    public readonly Enumerator GetEnumerator() => new(this);

    public readonly bool Equals(SlotBitmask<TLayout> other) => ReadOnlyWords.SequenceEqual(other.ReadOnlyWords);

    public readonly override bool Equals(object? obj) => obj is SlotBitmask<TLayout> other && Equals(other);

    public readonly override int GetHashCode()
    {
        HashCode hash = new();
        foreach (ulong word in ReadOnlyWords) hash.Add(word);
        return hash.ToHashCode();
    }

    /// <summary>The words in big-endian order, so that slot 0 reads as the rightmost bit.</summary>
    public readonly override string ToString()
    {
        ReadOnlySpan<ulong> words = ReadOnlyWords;
        Span<char> text = stackalloc char[words.Length * 16];
        for (int i = 0; i < words.Length; i++) words[^(i + 1)].TryFormat(text[(i * 16)..], out _, "x16");
        return new string(text);
    }

    /// <inheritdoc cref="GetEnumerator"/>
    public struct Enumerator
    {
        private readonly SlotBitmask<TLayout> _mask;
        private ulong _word;
        private int _wordIndex;

        internal Enumerator(SlotBitmask<TLayout> mask)
        {
            _mask = mask;
            _word = mask.ReadOnlyWords[0];
            _wordIndex = 0;
            Current = -1;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            while (_word == 0)
            {
                if (++_wordIndex >= TLayout.BoundaryMaskWordCount) return false;
                _word = _mask.ReadOnlyWords[_wordIndex];
            }

            Current = _wordIndex * PbtBitset.BitsPerWord + BitOperations.TrailingZeroCount(_word);
            _word &= _word - 1;
            return true;
        }
    }
}
