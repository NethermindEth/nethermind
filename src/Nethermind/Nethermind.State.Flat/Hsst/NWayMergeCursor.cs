// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Drives an N-way streaming merge across HSST enumerators using a winner tree (a.k.a.
/// tournament tree) over the per-source cached current-key spans. Find-min is O(log N)
/// after the initial O(N) build; matching-source detection on the winning key is still
/// linear (the merge bodies that consume <see cref="MatchingSources"/> need a dense list).
///
/// The cursor is intentionally allocation-free: all working memory lives in the caller-
/// supplied <see cref="LoserTreeState"/> (stack-allocated spans) plus a caller-supplied
/// <c>Span&lt;HsstEnumerator&gt;</c> for the per-slot iteration state. Per-source state —
/// the reader factory plus the bound this slot is positioned over — comes via a
/// <typeparamref name="TSource"/> per cursor slot; the cursor constructs an enumerator
/// per slot in its ctor via <typeparamref name="TFactory"/>. Newest-source-wins tie-break
/// is hard-coded; every live merge in <c>PersistedSnapshotMerger</c> wants this rule.
///
/// Usage:
/// <code>
/// // Caller rents sources + enumerators buffers and constructs the cursor:
/// NWayMergeCursor&lt;TReader, TPin, TSource, TFactory&gt; cursor = new(sources, enumerators, state, keyLen);
/// while (cursor.MoveNext())
/// {
///     // emit using cursor.MinKey;
///     // for nested merges, branch on cursor.MatchCount and consume cursor.MatchingSources.
///     cursor.AdvanceMatching();
/// }
/// </code>
/// </summary>
internal ref struct NWayMergeCursor<TReader, TPin, TSource, TFactory>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TSource : struct, IHsstMergeSource<TReader, TPin>
    where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
{
    private readonly Span<TSource> _sources;
    private readonly Span<HsstEnumerator<TReader, TPin>> _enumerators;
    // Cache the 4 state spans + stride at ctor so the hot loop stays Span-direct
    // (LoserTreeState's pool-backed properties construct a Span per access).
    private readonly Span<bool> _hasMore;
    private readonly Span<byte> _keyBuf;
    private readonly Span<int> _matchingBuf;
    private readonly Span<int> _tree;
    private readonly int _keyStride;
    private readonly int _n;
    private readonly int _pow2N;
    private readonly int _keyLen;

    private int _minIdx;
    private int _matchCount;

    /// <summary>Number of sources whose cached key equals <see cref="MinKey"/>.</summary>
    public readonly int MatchCount => _matchCount;

    /// <summary>
    /// Dense list of cursor slots whose cached key equals <see cref="MinKey"/>, in ascending
    /// slot order. View is backed by <c>state.MatchingBuf</c>; valid until the next <see cref="MoveNext"/>.
    /// </summary>
    public readonly ReadOnlySpan<int> MatchingSources => _matchingBuf[.._matchCount];

    /// <summary>
    /// Bytes of the current winner's logical key, length <c>keyLen</c>. Slice over the cached
    /// key buffer in the supplied <see cref="LoserTreeState"/>; valid until the next <see cref="AdvanceMatching"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> MinKey => _keyBuf.Slice(_minIdx * _keyStride, _keyLen);

    /// <summary>Logical key length in bytes (≤ <c>state.KeyStride</c>), as supplied to the ctor.</summary>
    public readonly int KeyLen => _keyLen;

    /// <summary>Value bound of the current winner's current entry. Valid after a true
    /// <see cref="MoveNext"/>, until <see cref="AdvanceMatching"/>.</summary>
    public readonly Bound MinValue => _enumerators[_minIdx].CurrentValue;

    /// <summary>Materialise a fresh reader for the current winner — routes to the winning
    /// source's <c>CreateReader()</c>. Each call constructs a new reader; the caller is
    /// responsible for its lifetime (typically a single <c>PinBuffer</c> + <c>using</c>).</summary>
    public readonly TReader CreateMinReader() => _sources[_minIdx].CreateReader();

    /// <summary>Value bound of source <paramref name="srcIdx"/>'s current entry. Valid while
    /// the source's cached key still equals <see cref="MinKey"/> (i.e. for slots present in
    /// <see cref="MatchingSources"/>, between <see cref="MoveNext"/> and the corresponding
    /// <see cref="AdvanceMatching"/>).</summary>
    public readonly Bound ValueAt(int srcIdx) => _enumerators[srcIdx].CurrentValue;

    /// <summary>The cursor's source span (one source per cursor slot). Used by nested-merge
    /// helpers that need the per-source reader factory list to build inner sources or to walk
    /// source bytes.</summary>
    public readonly Span<TSource> Sources => _sources;

    /// <param name="sources">N source structs, one per cursor slot. Each source supplies a
    /// reader factory and the bound this slot is positioned over.</param>
    /// <param name="enumerators">Caller-supplied buffer for the per-slot
    /// <see cref="HsstEnumerator{TReader,TPin}"/>s. Must be at least <c>sources.Length</c>
    /// elements; the ctor fills it via <paramref name="factory"/>.</param>
    /// <param name="state">Caller-allocated scratch (hasMore + keyBuf + matchingBuf + tree + keyStride).</param>
    /// <param name="keyLen">Logical key length in bytes (≤ <c>state.KeyStride</c>).</param>
    /// <param name="factory">Stateless dispatcher used to construct the per-slot enumerators
    /// from each source's reader + bound.</param>
    public NWayMergeCursor(
        Span<TSource> sources,
        Span<HsstEnumerator<TReader, TPin>> enumerators,
        LoserTreeState state,
        int keyLen,
        TFactory factory = default)
    {
        _sources = sources;
        _enumerators = enumerators;
        _hasMore = state.HasMore;
        _keyBuf = state.KeyBuf;
        _matchingBuf = state.MatchingBuf;
        _tree = state.Tree;
        _keyStride = state.KeyStride;
        _n = sources.Length;
        _keyLen = keyLen;
        _pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, _n));
        _minIdx = 0;
        _matchCount = 0;
        // Seed each source: construct the per-slot enumerator over its bound, MoveNext once
        // on it, cache the first key into _keyBuf for the tree compare. Sources that don't
        // have any entries leave _hasMore[i]=false (LoserTreeState's ctor pre-cleared the
        // array) so the tree treats them as +∞ losers.
        for (int i = 0; i < _n; i++)
        {
            TReader r = sources[i].CreateReader();
            _enumerators[i] = factory.Create(in r, sources[i].Bound);
            _hasMore[i] = _enumerators[i].MoveNext(in r);
            if (_hasMore[i])
                _enumerators[i].CopyCurrentLogicalKey(in r, _keyBuf.Slice(i * _keyStride, _keyLen));
        }
        Build();
    }

    /// <summary>
    /// Bottom-up O(N) winner-tree build off the primed cached keys. Internal node t at
    /// <c>state.Tree[t]</c> holds the winner of the match between its left and right child
    /// subtree winners; leaves (positions [pow2N, 2*pow2N-1]) are implicit (sourceIdx =
    /// leafIdx − pow2N). Padding leaves beyond <c>_n</c> are treated as +∞ losers.
    /// </summary>
    private void Build()
    {
        // For pow2N==1 (n==0 or n==1) the build loop is empty; tree[1] is the single leaf.
        if (_pow2N == 1)
        {
            _tree[1] = 0;
            return;
        }

        for (int t = _pow2N - 1; t >= 1; t--)
        {
            int left = 2 * t;
            int right = 2 * t + 1;
            int leftWinner = left >= _pow2N ? left - _pow2N : _tree[left];
            int rightWinner = right >= _pow2N ? right - _pow2N : _tree[right];
            _tree[t] = LessOrEqual(leftWinner, rightWinner) ? leftWinner : rightWinner;
        }
    }

    /// <summary>
    /// Returns true if source <paramref name="a"/> wins against <paramref name="b"/>.
    /// Sentinel (index ≥ n, or hasMore==false) always loses; on tied keys the higher
    /// source index (newer source) wins so terminal merges naturally pick newest-wins.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool LessOrEqual(int a, int b)
    {
        bool aLive = a < _n && _hasMore[a];
        bool bLive = b < _n && _hasMore[b];
        if (!aLive) return false;
        if (!bLive) return true;
        int cmp = _keyBuf.Slice(a * _keyStride, _keyLen)
            .SequenceCompareTo(_keyBuf.Slice(b * _keyStride, _keyLen));
        if (cmp != 0) return cmp < 0;
        return a > b;
    }

    /// <summary>
    /// Reads the current winner from the tree root. If the winner's source is exhausted,
    /// all sources are; returns false. Otherwise sets <see cref="MinKey"/>
    /// and rebuilds <see cref="MatchingSources"/> by an O(N) scan against the winner key.
    /// </summary>
    public bool MoveNext()
    {
        int champ = _tree[1];
        if (champ >= _n || !_hasMore[champ]) return false;
        _minIdx = champ;
        ReadOnlySpan<byte> minKey = _keyBuf.Slice(champ * _keyStride, _keyLen);
        int matchCount = 0;
        for (int i = 0; i < _n; i++)
        {
            if (!_hasMore[i]) continue;
            if (_keyBuf.Slice(i * _keyStride, _keyLen).SequenceEqual(minKey))
                _matchingBuf[matchCount++] = i;
        }
        _matchCount = matchCount;
        return true;
    }

    /// <summary>
    /// Advances every source in <see cref="MatchingSources"/>: calls <c>MoveNext</c> on the
    /// enumerator, refreshes the cached key, and updates the affected tree path (O(log N)
    /// per source). The cursor is ready for another <see cref="MoveNext"/> on return.
    /// </summary>
    public void AdvanceMatching()
    {
        for (int k = 0; k < _matchCount; k++)
        {
            int i = _matchingBuf[k];
            TReader r = _sources[i].CreateReader();
            _hasMore[i] = _enumerators[i].MoveNext(in r);
            if (_hasMore[i])
                _enumerators[i].CopyCurrentLogicalKey(in r, _keyBuf.Slice(i * _keyStride, _keyLen));
            UpdateLeaf(i);
        }
    }

    /// <summary>
    /// Single-leaf winner-tree update: walks leaf → root, replaying each match against the
    /// sibling subtree's stored winner and updating <c>state.Tree[parent]</c>. Sibling is found
    /// via <c>t XOR 1</c>; leaf siblings are implicit, internal siblings read <c>state.Tree</c>.
    /// </summary>
    private void UpdateLeaf(int sourceIdx)
    {
        if (_pow2N == 1) return;
        int t = _pow2N + sourceIdx;
        int winner = sourceIdx;
        while (t > 1)
        {
            int sibling = t ^ 1;
            int siblingWinner = sibling >= _pow2N ? sibling - _pow2N : _tree[sibling];
            if (!LessOrEqual(winner, siblingWinner)) winner = siblingWinner;
            t /= 2;
            _tree[t] = winner;
        }
    }
}
