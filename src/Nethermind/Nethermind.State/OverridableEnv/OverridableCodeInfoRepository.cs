// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        if (_precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile)) return precompile.codeInfo;

        if (_codeOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, ValueHash256 codeHash) result))
        {
            return !result.codeInfo.IsEmpty &&
                   CodeInfoRepository.TryGetDelegatedAddress(result.codeInfo.CodeSpan, out delegationAddress) &&
                   followDelegation
                ? GetDelegatedCodeInfo(delegationAddress)
                : result.codeInfo;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public CodeInfo GetDelegatedCodeInfo(Address delegationAddress)
    {
        if (_precompileOverrides.TryGetValue(delegationAddress, out (CodeInfo codeInfo, Address initialAddr) precompile))
        {
            return precompile.codeInfo;
        }

        return _codeOverrides.TryGetValue(delegationAddress, out (CodeInfo codeInfo, ValueHash256 codeHash) result)
            ? result.codeInfo
            : codeInfoRepository.GetDelegatedCodeInfo(delegationAddress);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value) => _codeOverrides[key] = (value, ValueKeccak.Compute(value.Code.Span));

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        _codeOverrides[precompileAddr] = (new CodeInfo(worldState.GetCode(precompileAddr)), worldState.GetCodeHash(precompileAddr));
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool HasDelegation(Address address)
    {
        if (_precompileOverrides.ContainsKey(address))
        {
            return false;
        }

        return _codeOverrides.TryGetValue(address, out (CodeInfo codeInfo, ValueHash256 codeHash) result)
            ? Eip7702Constants.IsDelegatedCode(result.codeInfo.CodeSpan)
            : codeInfoRepository.HasDelegation(address);
    }

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
