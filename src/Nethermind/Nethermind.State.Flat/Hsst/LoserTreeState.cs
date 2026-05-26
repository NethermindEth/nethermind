// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Numerics;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Self-allocated working memory for <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>'s
/// winner-tree algorithm. The four backing buffers (<see cref="HasMore"/>,
/// <see cref="KeyBuf"/>, <see cref="MatchingBuf"/>, <see cref="Tree"/>) are rented from
/// <see cref="ArrayPool{T}.Shared"/> in the ctor and returned in <see cref="Dispose"/>;
/// the typed <see cref="Span{T}"/> properties slice into them.
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
/// three buffers carry pool-residual content but the cursor overwrites every read
/// position before reading it.
/// </remarks>
internal ref struct LoserTreeState : IDisposable
{
    private readonly bool[] _hasMoreArr;
    private readonly byte[] _keyBufArr;
    private readonly int[] _matchingBufArr;
    private readonly int[] _treeArr;
    private readonly int _n;
    private readonly int _keyStride;

    public LoserTreeState(int n, int keyStride)
    {
        _n = n;
        _keyStride = keyStride;
        int safeN = Math.Max(1, n);
        _hasMoreArr = ArrayPool<bool>.Shared.Rent(safeN);
        _keyBufArr = ArrayPool<byte>.Shared.Rent(safeN * keyStride);
        _matchingBufArr = ArrayPool<int>.Shared.Rent(safeN);
        _treeArr = ArrayPool<int>.Shared.Rent(TreeLength(n));
        // Caller's seed loop sets hasMore[i]=true per live source; start from false.
        Array.Clear(_hasMoreArr, 0, safeN);
    }

    /// <summary>Per-source liveness flags; length N. Set to false when a source's
    /// enumerator exhausts so the loser-tree treats that slot as +∞.</summary>
    public readonly Span<bool> HasMore => _hasMoreArr.AsSpan(0, Math.Max(1, _n));

    /// <summary>Cached current-key bytes per source. Slot i lives at
    /// <c>KeyBuf[i*KeyStride .. i*KeyStride + keyLen]</c>; the cursor reads keys from here
    /// (not from each source's reader) during the O(log N) tournament walk.</summary>
    public readonly Span<byte> KeyBuf => _keyBufArr.AsSpan(0, Math.Max(1, _n) * _keyStride);

    /// <summary>Scratch for <see cref="NWayMergeCursor{TReader,TPin,TSource}.MatchingSources"/>;
    /// length ≥ N. Filled by <c>MoveNext</c>, consumed by <c>AdvanceMatching</c>.</summary>
    public readonly Span<int> MatchingBuf => _matchingBufArr.AsSpan(0, Math.Max(1, _n));

    /// <summary>Winner-tree backing storage; length ≥ <see cref="TreeLength"/>(N). Leaf slots
    /// at indices [pow2N, 2·pow2N) are implicit; internal nodes at [1, pow2N) carry the
    /// subtree winner.</summary>
    public readonly Span<int> Tree => _treeArr.AsSpan(0, TreeLength(_n));

    /// <summary>Stride (bytes per slot) in <see cref="KeyBuf"/>; ≥ <c>keyLen</c>.</summary>
    public readonly int KeyStride => _keyStride;

    public readonly void Dispose()
    {
        ArrayPool<bool>.Shared.Return(_hasMoreArr);
        ArrayPool<byte>.Shared.Return(_keyBufArr);
        ArrayPool<int>.Shared.Return(_matchingBufArr);
        ArrayPool<int>.Shared.Return(_treeArr);
    }

    /// <summary>Required <see cref="Tree"/> length for N sources: <c>2 × next-power-of-2(max(1, n))</c>.</summary>
    public static int TreeLength(int n)
        => 2 * (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, n));
}
