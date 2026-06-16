// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Self-allocated working memory for <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>'s
/// winner-tree algorithm. The four backing buffers (<see cref="HasMore"/>,
/// <see cref="KeyBuf"/>, <see cref="MatchingBuf"/>, <see cref="Tree"/>) are backed by
/// <see cref="NativeMemoryListRef{T}"/> allocated in the ctor and freed in <see cref="Dispose"/>;
/// the typed <see cref="Span{T}"/> properties slice into them. Native (unmovable) backing keeps
/// the spans/pointers used by the merge stable across GC.
/// </summary>
/// <remarks>
/// Typical use — one line at every merge call site:
/// <code>
/// using LoserTreeState state = new(n, keyStride);
/// // seed loop fills state.HasMore[i] and state.KeyBuf.Slice(i*keyStride, keyLen)
/// NWayMergeCursor&lt;TReader, TPin, TSource&gt; cursor = new(sources, state, keyLen);
/// </code>
/// The ctor pre-clears <see cref="HasMore"/> to false so the seed loop's
/// "set true when a source has data" pattern starts from a known baseline; the other
/// three buffers carry residual content but the cursor overwrites every read
/// position before reading it.
/// </remarks>
internal ref struct LoserTreeState : IDisposable
{
    private NativeMemoryListRef<bool> _hasMore;
    private NativeMemoryListRef<byte> _keyBuf;
    private NativeMemoryListRef<int> _matchingBuf;
    private NativeMemoryListRef<int> _tree;
    private readonly int _keyStride;

    public LoserTreeState(int n, int keyStride)
    {
        _keyStride = keyStride;
        int safeN = Math.Max(1, n);
        _hasMore = new NativeMemoryListRef<bool>(safeN, safeN);
        _keyBuf = new NativeMemoryListRef<byte>(safeN * keyStride, safeN * keyStride);
        _matchingBuf = new NativeMemoryListRef<int>(safeN, safeN);
        _tree = new NativeMemoryListRef<int>(TreeLength(n), TreeLength(n));
        _hasMore.AsSpan().Clear();
    }

    /// <summary>Per-source liveness flags; length N. Set to false when a source's
    /// enumerator exhausts so the loser-tree treats that slot as +∞.</summary>
    public readonly Span<bool> HasMore => _hasMore.AsSpan();

    /// <summary>Cached current-key bytes per source. Slot i lives at
    /// <c>KeyBuf[i*KeyStride .. i*KeyStride + keyLen]</c>; the cursor reads keys from here
    /// (not from each source's reader) during the O(log N) tournament walk.</summary>
    public readonly Span<byte> KeyBuf => _keyBuf.AsSpan();

    /// <summary>Scratch for <see cref="NWayMergeCursor{TReader,TPin,TSource}.MatchingSources"/>;
    /// length ≥ N. Filled by <c>MoveNext</c>, consumed by <c>AdvanceMatching</c>.</summary>
    public readonly Span<int> MatchingBuf => _matchingBuf.AsSpan();

    /// <summary>Winner-tree backing storage; length ≥ <see cref="TreeLength"/>(N). Leaf slots
    /// at indices [pow2N, 2·pow2N) are implicit; internal nodes at [1, pow2N) carry the
    /// subtree winner.</summary>
    public readonly Span<int> Tree => _tree.AsSpan();

    /// <summary>Stride (bytes per slot) in <see cref="KeyBuf"/>; ≥ <c>keyLen</c>.</summary>
    public readonly int KeyStride => _keyStride;

    public void Dispose()
    {
        _hasMore.Dispose();
        _keyBuf.Dispose();
        _matchingBuf.Dispose();
        _tree.Dispose();
    }

    public static int TreeLength(int n)
        => 2 * (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
}
