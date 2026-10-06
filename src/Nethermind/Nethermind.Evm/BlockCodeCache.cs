// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>An <see cref="ICodeCache"/> that also keeps the code a block loads until <see cref="ClearBlock"/>.</summary>
/// <remarks>
/// A warm CALL costs 100 gas whatever the size of the code, so a block can call more distinct large contracts, round
/// and round, than the process-wide cache holds; each call would then read and analyse its code again. Code loaded
/// during the block is kept here, up to <c>maxBytes</c> of it. Past that, code is not taken in rather than evicting
/// other code, as evicting on a cycle over a set larger than the cache misses on every load.
/// The process-wide cache is probed first, so a block it serves pays nothing here.
/// </remarks>
/// <param name="inner">The process-wide cache.</param>
/// <param name="maxBytes">The most code kept for the block, in bytes.</param>
public sealed class BlockCodeCache(ICodeCache inner, long maxBytes = BlockCodeCache.DefaultMaxBytes) : ICodeCache
{
    /// <summary>1 GiB: the code of 16k distinct contracts of 64 KiB.</summary>
    public const long DefaultMaxBytes = 1024L * 1024 * 1024;

    private readonly ConcurrentDictionary<ValueHash256, CodeInfo> _block = new();
    private long _bytes;

    /// <summary>The code bytes kept for the block.</summary>
    internal long Bytes => Volatile.Read(ref _bytes);

    public CodeInfo? Get(in ValueHash256 codeHash) =>
        inner.Get(in codeHash) ?? (Volatile.Read(ref _bytes) != 0 && _block.TryGetValue(codeHash, out CodeInfo? codeInfo) ? codeInfo : null);

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        inner.Set(in codeHash, codeInfo);

        // Racing loads may each pass the check, overshooting the cap by at most one code per thread.
        int length = codeInfo.CodeLength;
        if (Volatile.Read(ref _bytes) + length <= maxBytes && _block.TryAdd(codeHash, codeInfo))
        {
            Interlocked.Add(ref _bytes, length);
        }
    }

    /// <summary>Drops the code kept for the block.</summary>
    public void ClearBlock()
    {
        _block.Clear();
        Volatile.Write(ref _bytes, 0);
    }

    public void Clear()
    {
        ClearBlock();
        inner.Clear();
    }
}
