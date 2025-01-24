// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Evm;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, CodeInfo> _codeOverwrites = new();

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        return GetCachedCodeInfo(worldState, codeSource, true, vmSpec, out delegationAddress);
    }
    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        return _codeOverwrites.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);
    }

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(state, code, codeOwner, spec);

    public void SetCodeOverwrite(
        IWorldState worldState,
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress is not null)
        {
            _codeOverwrites[redirectAddress] = this.GetCachedCodeInfo(worldState, key, vmSpec);
        }

        _codeOverwrites[key] = value;
    }

    public void SetDelegation(IWorldState state, Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(state, codeSource, authority, spec);

    public bool TryGetDelegation(IReadOnlyStateProvider worldState, Address address, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(worldState, address, out delegatedAddress);

    public ValueHash256 GetExecutableCodeHash(IWorldState worldState, Address address) =>
        codeInfoRepository.GetExecutableCodeHash(worldState, address);

    public void ResetOverrides() => _codeOverwrites.Clear();
}
