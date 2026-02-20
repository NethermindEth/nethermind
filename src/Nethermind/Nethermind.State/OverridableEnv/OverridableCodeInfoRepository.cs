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

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, (CodeInfo codeInfo, ValueHash256 codeHash)> _codeOverrides = new();
    private readonly Dictionary<Address, (CodeInfo codeInfo, Address initialAddr)> _precompileOverrides = new();

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (TryGetPrecompileOverride(codeSource, out CodeInfo precompileCodeInfo))
        {
            return precompileCodeInfo;
        }

        if (TryGetCodeOverride(codeSource, out var overrideInfo))
        {
            return !overrideInfo.codeInfo.IsEmpty &&
                   ICodeInfoRepository.TryGetDelegatedAddress(overrideInfo.codeInfo.CodeSpan, out delegationAddress) &&
                   followDelegation
                ? GetCachedCodeInfo(delegationAddress, false, vmSpec, out Address? _)
                : overrideInfo.codeInfo;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public CodeInfo GetCachedCodeInfo(Address codeSource, in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        if (TryGetPrecompileOverride(codeSource, out CodeInfo precompileCodeInfo))
        {
            return precompileCodeInfo;
        }

        if (TryGetCodeOverride(codeSource, out var overrideInfo))
        {
            return overrideInfo.codeInfo;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, in codeHash, vmSpec);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value)
    {
        _codeOverrides[key] = (value, ValueKeccak.Compute(value.Code.Span));
    }

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        _codeOverrides[precompileAddr] = (new CodeInfo(worldState.GetCode(precompileAddr)), worldState.GetCodeHash(precompileAddr));
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec,
        [NotNullWhen(true)] out Address? delegatedAddress)
    {
        delegatedAddress = null;
        return _codeOverrides.TryGetValue(address, out var result)
            ? ICodeInfoRepository.TryGetDelegatedAddress(result.codeInfo.CodeSpan, out delegatedAddress)
            : codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);
    }


    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec) => _codeOverrides.TryGetValue(address, out var result)
        ? result.codeHash
        : codeInfoRepository.GetExecutableCodeHash(address, spec);

    private bool TryGetPrecompileOverride(Address codeSource, [NotNullWhen(true)] out CodeInfo? precompileCodeInfo)
    {
        if (_precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address _) precompileOverride))
        {
            precompileCodeInfo = precompileOverride.codeInfo;
            return true;
        }

        precompileCodeInfo = null;
        return false;
    }

    private bool TryGetCodeOverride(Address codeSource, out (CodeInfo codeInfo, ValueHash256 codeHash) codeOverride)
        => _codeOverrides.TryGetValue(codeSource, out codeOverride);

    public void ResetOverrides()
    {
        _precompileOverrides.Clear();
        _codeOverrides.Clear();
    }

    public void ResetPrecompileOverrides()
    {
        foreach (var (_, precompileInfo) in _precompileOverrides)
        {
            _codeOverrides.Remove(precompileInfo.initialAddr);
        }
        _precompileOverrides.Clear();
    }
}
