// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <remarks>
/// Implementation without any cache so that it always goes through
/// the world state, which captures touched bytecodes.
/// Relevant for witness generation (and therefore stateless reprocessing).
/// </remarks>
public class CodeInfoRepository : ICodeInfoRepository
{
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _localPrecompiles;
    private readonly IWorldState _worldState;
    /// <remarks>
    /// Kept null on the production path so <see cref="LoadCodeInfoDefault"/> can be called directly and inlined instead of going through a no-op delegate.
    /// </remarks>
    private readonly Func<Address, ValueHash256, IReleaseSpec, CodeInfo>? _codeInfoLoader;
    /// <summary>Precompile <see cref="CodeInfo"/> indexed by precompile number, for the low numbers.</summary>
    /// <remarks>Replaces a <see cref="FrozenDictionary{TKey, TValue}"/> hash and probe on every precompile
    /// call with an array index. Sized to the highest number the chain registers within the cap; anything
    /// above it — a plugin may register one far away, as Taiko does at 0x10001 — is left out and served by
    /// <see cref="_localPrecompiles"/> instead, so the array never has to cover the whole address space
    /// to be correct.</remarks>
    private readonly CodeInfo?[] _localPrecompileArray;

    public CodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
        : this(worldState, precompileProvider, codeInfoLoader: null)
    {
    }

    internal CodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider, Func<Address, ValueHash256, IReleaseSpec, CodeInfo>? codeInfoLoader)
    {
        _localPrecompiles = precompileProvider.GetPrecompiles();
        _worldState = worldState;
        _codeInfoLoader = codeInfoLoader;
        _localPrecompileArray = BuildPrecompileArray(_localPrecompiles);
    }

    /// <summary>Indexes the precompiles numbered at or below <c>MaxIndexedNumber</c>.</summary>
    /// <param name="precompiles">Every precompile the chain knows.</param>
    /// <returns>An array holding those within the cap, indexed by number, and nothing else.</returns>
    /// <remarks>The bound is taken from the precompiles themselves rather than fixed, so a chain adding
    /// one just above the last grows the array instead of falling off it, while a distant one costs a
    /// dictionary lookup rather than an array of its own size. The cap keeps a far-away number from
    /// turning into a huge mostly-empty array.</remarks>
    private static CodeInfo?[] BuildPrecompileArray(FrozenDictionary<AddressAsKey, CodeInfo> precompiles)
    {
        const int MaxIndexedNumber = 0x100;

        int highest = -1;
        foreach (AddressAsKey key in precompiles.Keys)
        {
            int index = ((Address)key).PrecompileIndexOrNegative();
            if (index > highest && index <= MaxIndexedNumber) highest = index;
        }

        if (highest < 0) return [];

        CodeInfo?[] byIndex = new CodeInfo?[highest + 1];
        foreach (KeyValuePair<AddressAsKey, CodeInfo> entry in precompiles)
        {
            int index = ((Address)entry.Key).PrecompileIndexOrNegative();
            if ((uint)index < (uint)byIndex.Length) byIndex[index] = entry.Value;
        }

        return byIndex;
    }

    public bool IsCodeOverridable => false;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (vmSpec.IsPrecompile(codeSource))
        {
            _worldState.AddAccountRead(codeSource);
            _worldState.RecordAccountAccess(codeSource);
            int index = codeSource.PrecompileIndexOrNegative();
            CodeInfo?[] byIndex = _localPrecompileArray;
            return (uint)index < (uint)byIndex.Length && byIndex[index] is { } precompile
                ? precompile
                : _localPrecompiles[codeSource];
        }

        CodeInfo codeInfo = InternalGetCodeInfo(codeSource, vmSpec);

        if (!codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
        {
            if (followDelegation)
            {
                codeInfo = InternalGetCodeInfo(delegationAddress, vmSpec);
            }
        }

        return codeInfo;
    }

    private CodeInfo InternalGetCodeInfo(Address codeSource, IReleaseSpec vmSpec)
    {
        ValueHash256 codeHash = _worldState.GetCodeHash(codeSource);
        Func<Address, ValueHash256, IReleaseSpec, CodeInfo>? codeInfoLoader = _codeInfoLoader;
        return codeInfoLoader is not null
            ? codeInfoLoader(codeSource, codeHash, vmSpec)
            : LoadCodeInfoDefault(codeSource, in codeHash);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CodeInfo LoadCodeInfoDefault(Address address, in ValueHash256 codeHash) =>
        codeHash == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : GetCodeInfo(_worldState, address, in codeHash);

    internal static CodeInfo GetCodeInfo(IWorldState worldState, Address address, in ValueHash256 codeHash)
    {
        // The one chokepoint where code is resolved by hash; record here so the witness also captures the account's trie path.
        worldState.RecordBytecodeAccess(address);
        // When executing in parallel must get by address
        byte[]? code = worldState.GetCode(in codeHash) ?? worldState.GetCode(address);
        if (code is null)
        {
            MissingCode(in codeHash);
        }

        // Counts code reads that miss the in-memory code cache (i.e. require a DB fetch via
        // IWorldState.GetCode). Cache hits served by CacheCodeInfoRepository.GetOrCacheCodeInfo
        // are counted separately as Metrics.CodeDbCache, so state_reads.code in the slow-block
        // JSON tracks DB-backed code reads only and equals cache.code.misses by construction.
        Metrics.IncrementCodeReads();
        Metrics.IncrementCodeBytesRead(code.Length);

        return CodeInfoFactory.CreateCodeInfo(code);

        [DoesNotReturn, StackTraceHidden]
        static void MissingCode(in ValueHash256 codeHash) => throw new DataException($"Code {codeHash} missing in the state");
    }


    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        InsertCode(_worldState, code, codeOwner, spec, out _);

    public static bool InsertCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec, out ValueHash256 codeHash)
    {
        codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        return worldState.InsertCode(codeOwner, in codeHash, code, spec);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        SetDelegation(_worldState, codeSource, authority, spec, out _, out _);

    public static bool SetDelegation(
        IWorldState worldState,
        Address codeSource,
        Address authority,
        IReleaseSpec spec,
        out ValueHash256 codeHash,
        out byte[] authorizedBuffer)
    {
        if (codeSource != Address.Zero)
        {
            authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
            Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
            codeSource.Bytes.CopyTo(authorizedBuffer.AsSpan(Eip7702Constants.DelegationHeader.Length));
            codeHash = ValueKeccak.Compute(authorizedBuffer);
        }
        else
        {
            authorizedBuffer = Array.Empty<byte>();
            codeHash = ValueKeccak.OfAnEmptyString;
        }

        bool result = worldState.InsertCode(authority, codeHash, authorizedBuffer, spec);
        if (result)
        {
            if (codeSource != Address.Zero)
            {
                Metrics.IncrementEip7702DelegationsSet();
            }
            else
            {
                Metrics.IncrementEip7702DelegationsCleared();
            }
        }

        return result;
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress)
    {
        if (!_worldState.HasCode(address))
        {
            delegatedAddress = null;
            return false;
        }

        return ICodeInfoRepository.TryGetDelegatedAddress(InternalGetCodeInfo(address, spec).CodeSpan, out delegatedAddress);
    }
}
