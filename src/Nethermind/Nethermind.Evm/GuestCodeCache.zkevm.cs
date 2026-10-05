// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>The zkVM guest's per-block <see cref="ICodeCache"/>: a plain map from code hash to code.</summary>
/// <remarks>
/// <see cref="StaticCodeCache"/> is built for concurrent readers and bounded memory, so every probe reads a seqlock
/// header and every hit stamps a clock, several times the cost of a map lookup on the guest. A block's code is bounded
/// by its witness and its gas (CREATE/CREATE2 deployments and EIP-7702 delegations also land here), and the guest
/// runs one thread, so nothing here needs either.
/// </remarks>
public sealed class GuestCodeCache(int capacity) : ICodeCache
{
    private readonly OptimizedDictionary<ValueHash256, CodeInfo> _codes = new(capacity);

    public CodeInfo? Get(in ValueHash256 codeHash) => _codes.TryGetValue(in codeHash, out CodeInfo? codeInfo) ? codeInfo : null;

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        // CacheCodeInfoRepository's memo matches on the stamped hash, as it does for StaticCodeCache.
        codeInfo.StampCodeHash(in codeHash);
        _codes[codeHash] = codeInfo;
    }

    public void Clear() => _codes.Clear();
}
