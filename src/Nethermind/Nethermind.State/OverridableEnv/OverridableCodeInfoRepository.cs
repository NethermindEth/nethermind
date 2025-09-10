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

namespace Nethermind.State.OverridableEnv;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, ICodeInfo> _codeOverwrites = new();

    public ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        return _codeOverwrites.TryGetValue(codeSource, out ICodeInfo result)
            ? result
            : codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public bool IsPrecompile(Address address, IReleaseSpec spec) =>
        _codeOverwrites.TryGetValue(address, out ICodeInfo result)
            ? result.IsPrecompile
            : codeInfoRepository.IsPrecompile(address, spec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverwrite(
        IReleaseSpec vmSpec,
        Address key,
        ICodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress is not null)
        {
            _codeOverwrites[redirectAddress] = this.GetCachedCodeInfo(key, vmSpec);
        }

        _codeOverwrites[key] = value;
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);

    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec) =>
        codeInfoRepository.GetExecutableCodeHash(address, spec);

    public void ResetOverrides() => _codeOverwrites.Clear();
}
