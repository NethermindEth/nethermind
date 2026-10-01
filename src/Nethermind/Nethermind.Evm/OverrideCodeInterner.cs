// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm;

/// <summary>Hands out one shared array for state-override code whose hex text was decoded before.</summary>
/// <remarks>
/// <para>
/// RPC clients replaying calls send the same override code again and again, and every request decoded its text
/// into a new array. Entries are keyed by the text itself: a hit needs the same hash and the same text byte for
/// byte, so different texts never share an array, even ones that decode to the same bytes. The hash covers the
/// length and three samples of a long text (see <see cref="HashOf"/>), so a hit reads the text once, to compare it.
/// </para>
/// <para>
/// Adding a text copies it, so a text is only added when it comes back: its first offer is remembered in a small
/// set-associative filter, the second one adds it and is forgotten, and a text that later falls out of the table has
/// to be offered twice again. The table keeps the entries in use (see <see cref="SetAssociativeTable{TEntry}"/>), so
/// when more texts come back than it holds, it keeps serving the ones it has instead of copying in a new one on every
/// miss. Code a client sends once costs a hash and no copy.
/// </para>
/// <para>
/// The table holds at most <see cref="Sets"/> × <see cref="Ways"/> entries of at most <see cref="MaxCodeLength"/>
/// bytes of code, each with its text, so it never keeps more than about 19 MB; longer code is decoded on every request.
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

    internal const int SeenSets = 256; // one per value of the top byte of the hash
    internal const int SeenWays = 4;

    /// <summary>The length of each sample <see cref="HashOf"/> takes of a long text.</summary>
    internal const int SampleLength = 256;

    public static OverrideCodeInterner Shared { get; } = new();

    private sealed class Entry(int fastHash, byte[] text, byte[] code) : SetAssociativeEntry(fastHash)
    {
        private readonly byte[] _text = text;
        public readonly byte[] Code = code;

        public override ReadOnlySpan<byte> Key => _text;
    }

    private readonly SetAssociativeTable<Entry> _entries = new(Sets, Ways, keepsUsedEntries: true);

    // The hashes of texts offered once and not added since, newest first in each set; 0 marks a free way.
    private readonly int[] _seen = new int[SeenSets * SeenWays];
    private readonly Lock _seenLock = new();

    internal int Count => _entries.Count;

    internal int RefusalsBeforeAging => _entries.RefusalsBeforeAging;

    /// <summary>The hash that picks the sets of <paramref name="text"/>.</summary>
    /// <remarks>
    /// Text up to four samples long is hashed whole; longer text by its length and its first, middle and last
    /// <see cref="SampleLength"/> bytes. Hashing a text whole would cost about as much as decoding it, while every hit
    /// is compared with the whole text anyway, so texts that differ only outside the samples share a set, never an
    /// array: at worst they are decoded on every request, as without the interner.
    /// </remarks>
    public static int HashOf(ReadOnlySpan<byte> text)
    {
        if (text.Length <= 4 * SampleLength) return text.FastHash();

        return HashCode.Combine(
            text.Length,
            text[..SampleLength].FastHash(),
            text.Slice((text.Length - SampleLength) / 2, SampleLength).FastHash(),
            text[^SampleLength..].FastHash());
    }

    /// <summary>Whether <paramref name="text"/> has a length the interner takes.</summary>
    /// <remarks>Text of two characters or fewer, such as <c>0x</c>, is left to the decoder.</remarks>
    public static bool Accepts(ReadOnlySpan<byte> text) => text.Length > 2 && text.Length <= MaxTextLength;

    /// <summary>Returns the array added for <paramref name="text"/>, or <see langword="null"/>.</summary>
    public byte[]? Find(ReadOnlySpan<byte> text, int fastHash) => _entries.Find(text, fastHash)?.Code;

    /// <summary>Offers <paramref name="code"/>, decoded from <paramref name="text"/>, to later requests.</summary>
    /// <remarks>The first offer of a text only remembers it, and an offer while the text's set has no room adds nothing.</remarks>
    public void Add(ReadOnlySpan<byte> text, int fastHash, byte[] code)
    {
        if (!Accepts(text) || !OfferedBefore(fastHash) || !_entries.HasRoomFor(fastHash)) return;

        _entries.GetOrAdd(new Entry(fastHash, text.ToArray(), code));
    }

    // Whether a text with this hash was offered before and not added since; forgets it if so, remembers it if not.
    private bool OfferedBefore(int fastHash)
    {
        // The top byte picks the set, as the low bits pick the set of the table.
        int mark = fastHash == 0 ? 1 : fastHash;
        Span<int> set = _seen.AsSpan((int)((uint)fastHash >> 24) * SeenWays, SeenWays);
        lock (_seenLock)
        {
            int way = set.IndexOf(mark);
            if (way >= 0)
            {
                set[way] = 0;
                return true;
            }

            set[..^1].CopyTo(set[1..]);
            set[0] = mark;
            return false;
        }
    }
}
