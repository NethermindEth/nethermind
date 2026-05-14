// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using HsstEnumerator = Nethermind.State.Flat.Hsst.HsstEnumerator<Nethermind.State.Flat.Storage.WholeReadSessionReader, Nethermind.State.Flat.Hsst.NoOpPin>;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Drives an N-way streaming merge across HSST enumerators using a winner tree (a.k.a.
/// tournament tree) over the per-source cached current-key spans. Find-min is O(log N)
/// after the initial O(N) build; matching-source detection on the winning key is still
/// linear (the merge bodies that consume <see cref="MatchingSources"/> need a dense list).
///
/// The cursor is intentionally allocation-free: all working memory (the cached-key buffer,
/// the matching-source buffer, and the tree backing storage) is supplied by the caller as
/// spans — stack allocations at the call site are typical. Enumerator state lives in the
/// caller-owned <c>HsstEnumerator[]</c>; the cursor mutates the <c>hasMore</c> flags and
/// the cached keys as it advances. Newest-source-wins tie-break is hard-coded; every live
/// merge in <see cref="PersistedSnapshotMerger"/> wants this rule.
///
/// Usage:
/// <code>
/// // Caller primes enumerators + first key per source, then constructs the cursor:
/// NWayMergeCursor cursor = new(enums, hasMore, views, srcMap, n, keyLen, keyStride,
///                              keyBuf, matchingBuf, tree);
/// while (cursor.MoveNext())
/// {
///     // emit at cursor.MinIdx using cursor.MinKey;
///     // for nested merges, branch on cursor.MatchCount and consume cursor.MatchingSources.
///     cursor.AdvanceMatching();
/// }
/// </code>
/// </summary>
internal ref struct NWayMergeCursor
{
    private readonly HsstEnumerator[] _enums;
    private readonly Span<bool> _hasMore;
    private readonly ReadOnlySpan<(IntPtr Ptr, long Len)> _views;
    private readonly ReadOnlySpan<int> _sourceMap;
    private readonly Span<byte> _keyBuf;
    private readonly Span<int> _matchingBuf;
    private readonly Span<int> _tree;
    private readonly int _n;
    private readonly int _pow2N;
    private readonly int _keyLen;
    private readonly int _keyStride;

    private int _minIdx;
    private int _matchCount;

    /// <summary>Cursor slot of the current winner. Valid after a true <see cref="MoveNext"/>.</summary>
    public readonly int MinIdx => _minIdx;

    /// <summary>Number of sources whose cached key equals <see cref="MinKey"/>.</summary>
    public readonly int MatchCount => _matchCount;

    /// <summary>
    /// Dense list of cursor slots whose cached key equals <see cref="MinKey"/>, in ascending
    /// slot order. View is backed by the matchingBuf the caller supplied at construction; it
    /// stays valid until the next <see cref="MoveNext"/>.
    /// </summary>
    public readonly ReadOnlySpan<int> MatchingSources => _matchingBuf[.._matchCount];

    /// <summary>
    /// Bytes of the current winner's logical key, length <c>keyLen</c>. Slice over the cached
    /// key buffer the caller supplied; stays valid until the next <see cref="AdvanceMatching"/>.
    /// </summary>
    public readonly ReadOnlySpan<byte> MinKey => _keyBuf.Slice(_minIdx * _keyStride, _keyLen);

    /// <param name="enums">Per-cursor-slot enumerators; element i is already <c>MoveNext</c>'d once.</param>
    /// <param name="hasMore">Per-cursor-slot has-more flag; aligned with <paramref name="enums"/>.</param>
    /// <param name="views">Global view table; the cursor reads slot <c>sourceMap[i]</c> when refilling source <c>i</c>.</param>
    /// <param name="sourceMap">cursorSlot → views index. Identity map for top-level merges; subset map for nested ones.</param>
    /// <param name="n">Number of cursor slots actually populated (≤ <paramref name="enums"/>.Length).</param>
    /// <param name="keyLen">Logical key length in bytes.</param>
    /// <param name="keyStride">Bytes per slot in <paramref name="keyBuf"/>; ≥ keyLen.</param>
    /// <param name="keyBuf">Cached keys, slot i at <c>keyBuf[i * keyStride .. i * keyStride + keyLen]</c>. Caller primes slots with hasMore[i]==true before construction.</param>
    /// <param name="matchingBuf">Scratch for <see cref="MatchingSources"/>; length ≥ n.</param>
    /// <param name="tree">Winner-tree backing; length ≥ 2 × next-power-of-2(n).</param>
    public NWayMergeCursor(
        HsstEnumerator[] enums,
        Span<bool> hasMore,
        ReadOnlySpan<(IntPtr Ptr, long Len)> views,
        ReadOnlySpan<int> sourceMap,
        int n,
        int keyLen,
        int keyStride,
        Span<byte> keyBuf,
        Span<int> matchingBuf,
        Span<int> tree)
    {
        _enums = enums;
        _hasMore = hasMore;
        _views = views;
        _sourceMap = sourceMap;
        _n = n;
        _keyLen = keyLen;
        _keyStride = keyStride;
        _keyBuf = keyBuf;
        _matchingBuf = matchingBuf;
        _tree = tree;
        _pow2N = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
        _minIdx = 0;
        _matchCount = 0;
        Build();
    }

    /// <summary>
    /// Bottom-up O(N) winner-tree build off the primed cached keys. Internal node t at
    /// <c>_tree[t]</c> holds the winner of the match between its left and right child
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
        int cmp = _keyBuf.Slice(a * _keyStride, _keyLen).SequenceCompareTo(_keyBuf.Slice(b * _keyStride, _keyLen));
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
            WholeReadSessionReader r = Reader(_views[_sourceMap[i]]);
            _hasMore[i] = _enums[i].MoveNext(in r);
            if (_hasMore[i])
                _enums[i].CopyCurrentLogicalKey(in r, _keyBuf.Slice(i * _keyStride, _keyLen));
            UpdateLeaf(i);
        }
    }

    /// <summary>
    /// Single-leaf winner-tree update: walks leaf → root, replaying each match against the
    /// sibling subtree's stored winner and updating <c>_tree[parent]</c>. Sibling is found
    /// via <c>t XOR 1</c>; leaf siblings are implicit, internal siblings read <c>_tree</c>.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static WholeReadSessionReader Reader((IntPtr Ptr, long Len) v)
    {
        unsafe { return new WholeReadSessionReader((byte*)v.Ptr, v.Len); }
    }
}
