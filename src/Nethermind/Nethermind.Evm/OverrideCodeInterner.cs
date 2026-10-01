// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm;

/// <summary>Hands out one shared array for state-override code whose hex text was decoded before.</summary>
/// <remarks>
/// <para>
/// RPC clients replaying calls send the same override code again and again, and every request decoded its text
/// into a new array. Entries are keyed by the text itself: a hit needs the same hash and the same text byte for
/// byte, so different texts never share an array, even ones that decode to the same bytes.
/// </para>
/// <para>
/// A text is added the second time it is offered, so code a client sends once costs a hash and no copy. The table
/// holds at most <see cref="Sets"/> × <see cref="Ways"/> entries of at most <see cref="MaxCodeLength"/> bytes of
/// code, each with its text, so it never keeps more than about 19 MB; longer code is decoded on every request.
/// </para>
/// <para>
/// Every request that sends a text gets the same array, so nothing may write to it, as the world state and
/// <see cref="OverrideCodeCache"/> already assume of override code.
/// </para>
/// </remarks>
internal sealed class OverrideCodeInterner
{
    internal const int Sets = 64; // power of two
    internal const int Ways = 4;

    /// <summary>The longest code interned: the EIP-170 contract size limit.</summary>
    internal const int MaxCodeLength = 24 * 1024;

    private const int MaxTextLength = 2 + 2 * MaxCodeLength;
    private const int SeenSlots = 1024; // power of two

    public static OverrideCodeInterner Shared { get; } = new();

    private sealed class Entry(int fastHash, byte[] text, byte[] code) : SetAssociativeEntry(fastHash)
    {
        private readonly byte[] _text = text;
        public readonly byte[] Code = code;

        public override ReadOnlySpan<byte> Key => _text;
    }

    private readonly SetAssociativeTable<Entry> _entries = new(Sets, Ways);

    // The hash of the last text offered once, per slot; a text is added when it comes back.
    private readonly int[] _seen = new int[SeenSlots];

    internal int Count => _entries.Count;

    /// <summary>Whether <paramref name="text"/> has a length the interner takes.</summary>
    /// <remarks>Text of two characters or fewer, such as <c>0x</c>, is left to the decoder.</remarks>
    public static bool Accepts(ReadOnlySpan<byte> text) => text.Length > 2 && text.Length <= MaxTextLength;

    /// <summary>Returns the array added for <paramref name="text"/>, or <see langword="null"/>.</summary>
    public byte[]? Find(ReadOnlySpan<byte> text, int fastHash) => _entries.Find(text, fastHash)?.Code;

    /// <summary>Offers <paramref name="code"/>, decoded from <paramref name="text"/>, to later requests.</summary>
    /// <remarks>The first offer of a text only marks it as seen.</remarks>
    public void Add(ReadOnlySpan<byte> text, int fastHash, byte[] code)
    {
        if (!Accepts(text)) return;

        ref int seen = ref _seen[fastHash & (SeenSlots - 1)];
        if (seen != fastHash)
        {
            seen = fastHash;
            return;
        }

        _entries.GetOrAdd(new Entry(fastHash, text.ToArray(), code));
    }
}
