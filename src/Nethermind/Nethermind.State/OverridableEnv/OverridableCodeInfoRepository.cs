// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, ICodeCache codeCache) : IOverridableCodeInfoRepository
{
    public OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState)
        : this(codeInfoRepository, worldState, NoopCodeCache.Instance) { }

    private readonly Dictionary<Address, (CodeInfo codeInfo, ValueHash256 codeHash)> _codeOverrides = [];
    private readonly Dictionary<Address, (CodeInfo codeInfo, Address initialAddr)> _precompileOverrides = [];

    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (_precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile)) return precompile.codeInfo;

        if (_codeOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, ValueHash256 codeHash) result))
        {
            return !result.codeInfo.IsEmpty &&
                   ICodeInfoRepository.TryGetDelegatedAddress(result.codeInfo.CodeSpan, out delegationAddress) &&
                   followDelegation
                ? GetCachedCodeInfo(delegationAddress, false, vmSpec, out Address? _)
                : result.codeInfo;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    /// <inheritdoc/>
    /// <remarks>
    /// Identical code is served by one <see cref="CodeInfo"/> across requests through the overrides' code cache:
    /// the jump-destination analysis is reused, and the instruction stream is reachable at all, as its build
    /// threshold counts hits per instance and its cache is keyed by <see cref="CodeInfo.CodeHash"/> - a fresh,
    /// hash-less instance per request would never get there. The code is request-supplied, so only code within
    /// the spec's code-size limit is cached, keeping the cache's footprint bounded like that of on-chain code.
    /// </remarks>
    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value)
    {
        ValueHash256 codeHash = value.CodeHash != default ? value.CodeHash : ValueKeccak.Compute(value.Code.Span);
        bool cacheable = !value.IsEmpty && value.Code.Length <= vmSpec.MaxCodeSize;
        CodeInfo? shared = cacheable ? codeCache.Get(in codeHash) : null;
        if (shared is null)
        {
            value.CodeHash = codeHash;
            if (cacheable)
            {
                codeCache.Set(in codeHash, value);
            }

            shared = value;
        }

        _codeOverrides[key] = (shared, codeHash);
    }

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        ValueHash256 movedCodeHash = worldState.GetCodeHash(precompileAddr);
        _codeOverrides[precompileAddr] = (new CodeInfo(worldState.GetCode(precompileAddr)) { CodeHash = movedCodeHash }, movedCodeHash);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        _codeOverrides.TryGetValue(address, out (CodeInfo codeInfo, ValueHash256 codeHash) result)
            ? ICodeInfoRepository.TryGetDelegatedAddress(result.codeInfo.CodeSpan, out delegatedAddress)
            : codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);


    public void ResetOverrides()
    {
        _precompileOverrides.Clear();
        _codeOverrides.Clear();
    }

    public void ResetPrecompileOverrides()
    {
        foreach ((Address _, (CodeInfo codeInfo, Address initialAddr) precompileInfo) in _precompileOverrides)
        {
            _codeOverrides.Remove(precompileInfo.initialAddr);
        }
        _precompileOverrides.Clear();
    }
}
