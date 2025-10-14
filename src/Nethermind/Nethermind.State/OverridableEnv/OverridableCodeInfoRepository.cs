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
    private readonly Dictionary<Address, ICodeInfo> _codeOverrides = new();
    private readonly Dictionary<Address, (ICodeInfo codeInfo, Address initialAddr)> _precompileOverrides = new();

    public ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (_precompileOverrides.TryGetValue(codeSource, out var precompile)) return precompile.codeInfo;

        return _codeOverrides.TryGetValue(codeSource, out ICodeInfo result)
            ? result
            : codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        ICodeInfo value)
    {
        _codeOverrides[key] = value;
    }

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        // TODO: fix
        _codeOverrides[precompileAddr] = new CodeInfo(worldState.GetCode(precompileAddr));
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);

    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec) =>
        codeInfoRepository.GetExecutableCodeHash(address, spec);

    public void ResetOverrides() => _codeOverrides.Clear();
    public void ResetPrecompileOverrides()
    {
        foreach (var (_, precompileInfo) in _precompileOverrides)
        {
            _codeOverrides.Remove(precompileInfo.initialAddr);
        }
        _precompileOverrides.Clear();
    }
}
