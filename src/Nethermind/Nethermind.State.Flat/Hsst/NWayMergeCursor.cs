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
/// supplied <see cref="LoserTreeState"/> (stack-allocated spans). Per-source state — the
/// HSST enumerator plus the means to construct a reader — comes via a
/// <typeparamref name="TSource"/> ref-struct per cursor slot. Newest-source-wins tie-break
/// is hard-coded; every live merge in <c>PersistedSnapshotMerger</c> wants this rule.
///
/// Usage:
/// <code>
/// // Caller primes enumerators + first key per source, then constructs the cursor:
/// NWayMergeCursor&lt;TReader, TPin, TSource&gt; cursor = new(sources, state, keyLen);
/// while (cursor.MoveNext())
/// {
///     // emit at cursor.MinIdx using cursor.MinKey;
///     // for nested merges, branch on cursor.MatchCount and consume cursor.MatchingSources.
///     cursor.AdvanceMatching();
/// }
/// </code>
/// </summary>
internal ref struct NWayMergeCursor<TReader, TPin, TSource>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TSource : struct, IHsstMergeSource<TReader, TPin>
{
    private readonly Span<TSource> _sources;
    private readonly LoserTreeState _state;
    private readonly int _n;
    private readonly int _pow2N;
    private readonly int _keyLen;

    private int _minIdx;
    private int _matchCount;

    /// <summary>Cursor slot of the current winner. Valid after a true <see cref="MoveNext"/>.</summary>
    public readonly int MinIdx => _minIdx;

    /// <summary>Number of sources whose cached key equals <see cref="MinKey"/>.</summary>
    public readonly int MatchCount => _matchCount;

    /// <summary>
    /// Dense list of cursor slots whose cached key equals <see cref="MinKey"/>, in ascending
    /// slot order. View is backed by <c>state.MatchingBuf</c>; valid until the next <see cref="MoveNext"/>.
    /// </summary>
    public readonly ReadOnlySpan<int> MatchingSources => _state.MatchingBuf[.._matchCount];

    /// <summary>
    /// Bytes of the current winner's logical key, length <c>keyLen</c>. Slice over the cached
    /// key buffer in the supplied <see cref="LoserTreeState"/>; valid until the next <see cref="AdvanceMatching"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> MinKey => _state.KeyBuf.Slice(_minIdx * _state.KeyStride, _keyLen);

    /// <param name="sources">N source structs, one per cursor slot, already primed
    /// (each source's enumerator <c>MoveNext</c>'d once, key copied into <c>state.KeyBuf</c>,
    /// <c>state.HasMore[i]</c> set accordingly).</param>
    /// <param name="state">Caller-allocated scratch (hasMore + keyBuf + matchingBuf + tree + keyStride).</param>
    /// <param name="keyLen">Logical key length in bytes (≤ <c>state.KeyStride</c>).</param>
    public NWayMergeCursor(
        Span<TSource> sources,
        LoserTreeState state,
        int keyLen)
    {
        _sources = sources;
        _state = state;
        _n = sources.Length;
        _keyLen = keyLen;
        _pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, _n));
        _minIdx = 0;
        _matchCount = 0;
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
            _state.Tree[1] = 0;
            return;
        }

        for (int t = _pow2N - 1; t >= 1; t--)
        {
            int left = 2 * t;
            int right = 2 * t + 1;
            int leftWinner = left >= _pow2N ? left - _pow2N : _state.Tree[left];
            int rightWinner = right >= _pow2N ? right - _pow2N : _state.Tree[right];
            _state.Tree[t] = LessOrEqual(leftWinner, rightWinner) ? leftWinner : rightWinner;
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
        bool aLive = a < _n && _state.HasMore[a];
        bool bLive = b < _n && _state.HasMore[b];
        if (!aLive) return false;
        if (!bLive) return true;
        int cmp = _state.KeyBuf.Slice(a * _state.KeyStride, _keyLen)
            .SequenceCompareTo(_state.KeyBuf.Slice(b * _state.KeyStride, _keyLen));
        if (cmp != 0) return cmp < 0;
        return a > b;
    }

    /// <summary>
    /// Reads the current winner from the tree root. If the winner's source is exhausted,
    /// all sources are; returns false. Otherwise sets <see cref="MinIdx"/>/<see cref="MinKey"/>
    /// and rebuilds <see cref="MatchingSources"/> by an O(N) scan against the winner key.
    /// </summary>
    public bool MoveNext()
    {
        int champ = _state.Tree[1];
        if (champ >= _n || !_state.HasMore[champ]) return false;
        _minIdx = champ;
        ReadOnlySpan<byte> minKey = _state.KeyBuf.Slice(champ * _state.KeyStride, _keyLen);
        int matchCount = 0;
        for (int i = 0; i < _n; i++)
        {
            if (!_state.HasMore[i]) continue;
            if (_state.KeyBuf.Slice(i * _state.KeyStride, _keyLen).SequenceEqual(minKey))
                _state.MatchingBuf[matchCount++] = i;
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
            int i = _state.MatchingBuf[k];
            TReader r = _sources[i].CreateReader();
            HsstEnumerator<TReader, TPin> e = _sources[i].GetEnumerator();
            _state.HasMore[i] = e.MoveNext(in r);
            if (_state.HasMore[i])
                e.CopyCurrentLogicalKey(in r, _state.KeyBuf.Slice(i * _state.KeyStride, _keyLen));
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
            int siblingWinner = sibling >= _pow2N ? sibling - _pow2N : _state.Tree[sibling];
            if (!LessOrEqual(winner, siblingWinner)) winner = siblingWinner;
            t /= 2;
            _state.Tree[t] = winner;
        }
    }
}
