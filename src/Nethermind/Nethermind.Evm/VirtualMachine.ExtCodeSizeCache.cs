// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.EvmObjectFormat;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    private const int MaxExtCodeSizeCacheEntries = 256;
    private Dictionary<AddressAsKey, ExtCodeSizeCacheEntry>? _extCodeSizeCache;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint GetExtCodeSizeCached(Address address, IReleaseSpec spec)
    {
        _extCodeSizeCache ??= new Dictionary<AddressAsKey, ExtCodeSizeCacheEntry>(8);
        AddressAsKey key = address;

        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (_extCodeSizeCache.TryGetValue(key, out ExtCodeSizeCacheEntry entry) && entry.CodeHash == codeHash)
        {
            return entry.CodeSize;
        }

        ReadOnlySpan<byte> code = _codeInfoRepository.GetCachedCodeInfo(address, followDelegation: false, spec, out _).CodeSpan;
        uint codeSize = spec.IsEofEnabled && EofValidator.IsEof(code, out _) ? 2u : (uint)code.Length;

        if (_extCodeSizeCache.Count >= MaxExtCodeSizeCacheEntries)
        {
            _extCodeSizeCache.Clear();
        }
        _extCodeSizeCache[key] = new ExtCodeSizeCacheEntry(codeHash, codeSize);
        return codeSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetExtCodeSizeCache()
    {
        // Cache is keyed by code hash to remain correct when code changes mid-transaction.
        _extCodeSizeCache?.Clear();
    }

    private readonly record struct ExtCodeSizeCacheEntry(ValueHash256 CodeHash, uint CodeSize);
}
