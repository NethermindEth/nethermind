// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

public class CacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider) : CodeInfoRepository(worldState, precompileProvider)
{
    private static readonly CodeLruCache _codeCache = new();

    protected override ICodeInfo InternalGetCodeInfo(in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        ICodeInfo? cachedCodeInfo = null;
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            cachedCodeInfo = CodeInfo.Empty;
        }

        cachedCodeInfo ??= _codeCache.Get(in codeHash);
        if (cachedCodeInfo is null)
        {
            byte[]? code = _worldState.GetCode(in codeHash);

            if (code is null)
            {
                MissingCode(in codeHash);
            }

            cachedCodeInfo = CodeInfoFactory.CreateCodeInfo(code, vmSpec, ValidationStrategy.ExtractHeader);
            _codeCache.Set(in codeHash, cachedCodeInfo);
        }
        else
        {
            Metrics.IncrementCodeDbCache();
        }

        return cachedCodeInfo;

        [DoesNotReturn, StackTraceHidden]
        static void MissingCode(in ValueHash256 codeHash)
        {
            throw new DataException($"Code {codeHash} missing in the state");
        }
    }

    public override void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        // If the code is already in the cache, we don't need to create and add it again (and reanalyze it)
        if (_worldState.InsertCode(codeOwner, in codeHash, code, spec) &&
            _codeCache.Get(in codeHash) is null)
        {
            ICodeInfo codeInfo = CodeInfoFactory.CreateCodeInfo(code, spec, ValidationStrategy.ExtractHeader);
            _codeCache.Set(in codeHash, codeInfo);
        }
    }

    public override void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        if (codeSource == Address.Zero)
        {
            _worldState.InsertCode(authority, Keccak.OfAnEmptyString, Array.Empty<byte>(), spec);
            return;
        }
        byte[] authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
        Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
        codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
        ValueHash256 codeHash = ValueKeccak.Compute(authorizedBuffer);
        if (_worldState.InsertCode(authority, codeHash, authorizedBuffer.AsMemory(), spec)
            // If the code is already in the cache, we don't need to create CodeInfo and add it again (and reanalyze it)
            && _codeCache.Get(in codeHash) is null)
        {
            _codeCache.Set(codeHash, new CodeInfo(authorizedBuffer));
        }
    }

    public bool TryGetDelegation(in ValueHash256 codeHash, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        ICodeInfoRepository.TryGetDelegatedAddress(InternalGetCodeInfo(in codeHash, spec).CodeSpan, out delegatedAddress);

    private sealed class CodeLruCache
    {
        private const int CacheCount = 16;
        private const int CacheMax = CacheCount - 1;
        private readonly ClockCache<ValueHash256, ICodeInfo>[] _caches;

        public CodeLruCache()
        {
            _caches = new ClockCache<ValueHash256, ICodeInfo>[CacheCount];
            for (int i = 0; i < _caches.Length; i++)
            {
                // Cache per nibble to reduce contention as TxPool is very parallel
                _caches[i] = new ClockCache<ValueHash256, ICodeInfo>(MemoryAllowance.CodeCacheSize / CacheCount);
            }
        }

        public ICodeInfo? Get(in ValueHash256 codeHash)
        {
            ClockCache<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Get(codeHash);
        }

        public bool Set(in ValueHash256 codeHash, ICodeInfo codeInfo)
        {
            ClockCache<ValueHash256, ICodeInfo> cache = _caches[GetCacheIndex(codeHash)];
            return cache.Set(codeHash, codeInfo);
        }

        private static int GetCacheIndex(in ValueHash256 codeHash) => codeHash.Bytes[^1] & CacheMax;

        public bool TryGet(in ValueHash256 codeHash, [NotNullWhen(true)] out ICodeInfo? codeInfo)
        {
            codeInfo = Get(in codeHash);
            return codeInfo is not null;
        }
    }
}

