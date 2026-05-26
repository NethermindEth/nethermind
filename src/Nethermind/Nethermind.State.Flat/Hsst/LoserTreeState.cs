// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Caller-allocated working memory for <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>'s
/// winner-tree algorithm. Bundling the four spans + the key stride into one struct keeps the
/// cursor's ctor narrow and makes it obvious which buffers are scratch the cursor owns the
/// reads/writes of (rather than per-source source state, which lives on <see cref="IHsstMergeSource{TReader,TPin}"/>).
/// </summary>
/// <remarks>
/// Typical use:
/// <code>
/// int n = sources.Length;
/// int keyStride = keyLen;
/// Span&lt;bool&gt; hasMore = stackalloc bool[n];
/// Span&lt;byte&gt; keyBuf = stackalloc byte[n * keyStride];
/// Span&lt;int&gt; matchingBuf = stackalloc int[n];
/// Span&lt;int&gt; tree = stackalloc int[LoserTreeState.TreeLength(n)];
/// LoserTreeState state = new(hasMore, keyBuf, matchingBuf, tree, keyStride);
/// </code>
/// All allocations are stack-local; the cursor pays zero heap per merge.
/// </remarks>
internal readonly ref struct LoserTreeState
{
    /// <summary>Per-source liveness flags; length N. Set to false when a source's
    /// enumerator exhausts so the loser-tree treats that slot as +∞.</summary>
    public Span<bool> HasMore { get; }

    /// <summary>Cached current-key bytes per source. Slot i lives at
    /// <c>KeyBuf[i*KeyStride .. i*KeyStride + keyLen]</c>; the cursor reads keys from here
    /// (not from each source's reader) during the O(log N) tournament walk.</summary>
    public Span<byte> KeyBuf { get; }

    /// <summary>Scratch for <see cref="NWayMergeCursor{TReader,TPin,TSource}.MatchingSources"/>;
    /// length ≥ N. Filled by <c>MoveNext</c>, consumed by <c>AdvanceMatching</c>.</summary>
    public Span<int> MatchingBuf { get; }

    /// <summary>Winner-tree backing storage; length ≥ <see cref="TreeLength"/>(N). Leaf slots
    /// at indices [pow2N, 2·pow2N) are implicit; internal nodes at [1, pow2N) carry the
    /// subtree winner.</summary>
    public Span<int> Tree { get; }

    /// <summary>Stride (bytes per slot) in <see cref="KeyBuf"/>; ≥ <c>keyLen</c>.</summary>
    public int KeyStride { get; }

    public LoserTreeState(
        Span<bool> hasMore,
        Span<byte> keyBuf,
        Span<int> matchingBuf,
        Span<int> tree,
        int keyStride)
    {
        HasMore = hasMore;
        KeyBuf = keyBuf;
        MatchingBuf = matchingBuf;
        Tree = tree;
        KeyStride = keyStride;
    }

    /// <summary>Required <see cref="Tree"/> length for N sources: <c>2 × next-power-of-2(max(1, n))</c>.</summary>
    public static int TreeLength(int n)
        => 2 * (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
}
