// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>LRU <see cref="ICodeCache"/>; <see cref="Instance"/> is the process-wide one used for normal block processing and reset between shared-cache test runs.</summary>
/// <remarks>Capacities below <see cref="MemoryAllowance.CodeCacheSize"/> are for short-lived,
/// single-block caches; overflowing one only costs a re-read and re-analysis of the code.</remarks>
public sealed class StaticCodeCache(int maxCapacity) : ICodeCache
{
    public static readonly StaticCodeCache Instance = new(
        ExperimentSwitches.Int("NM_XP_CODE_CACHE", MemoryAllowance.CodeCacheSize));

    private readonly AssociativeCache<ValueHash256, CodeInfo> _cache = new(maxCapacity);

    public CodeInfo? Get(in ValueHash256 codeHash) => _cache.Get(in codeHash);

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo)
    {
        codeInfo.CodeHash = codeHash;
        _cache.Set(in codeHash, codeInfo);
    }

    public void Clear() => _cache.Clear();
}
