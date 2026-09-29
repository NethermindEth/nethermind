// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, CodeOverrideStore? overrides = null) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, CodeInfo> _codeOverrides = (overrides ??= new CodeOverrideStore()).Code;
    private readonly Dictionary<Address, (CodeInfo codeInfo, Address initialAddr)> _precompileOverrides = overrides.Precompiles;

    /// <summary>Precompile addresses whose code is overridden, moved-away origins included.</summary>
    /// <remarks>They dispatch as code only for the block that overrides them.</remarks>
    private readonly HashSet<Address> _overriddenPrecompileAddresses = [];

    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        // Moved precompiles are rare, so skip the hash lookup when there are none.
        if (_precompileOverrides.Count != 0 && _precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile)) return precompile.codeInfo;

        if (_codeOverrides.TryGetValue(codeSource, out CodeInfo? result))
        {
            return !result.IsEmpty &&
                   ICodeInfoRepository.TryGetDelegatedAddress(result.CodeSpan, out delegationAddress) &&
                   followDelegation
                ? GetDelegatedCodeInfo(delegationAddress, vmSpec)
                : result;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A code override, including one at a precompile's address, runs as code. A move destination resolves to its
    /// own code, not to the precompile moved there.
    /// </remarks>
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec) =>
        _codeOverrides.TryGetValue(target, out CodeInfo? result) ? result : codeInfoRepository.GetDelegatedCodeInfo(target, vmSpec);

    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        _precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile) ? precompile.codeInfo.Precompile
        : _codeOverrides.TryGetValue(codeSource, out CodeInfo? result) ? result.Precompile
        : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value)
    {
        _codeOverrides[key] = value;
        if (vmSpec.IsPrecompile(key)) _overriddenPrecompileAddresses.Add(key);
    }

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        _codeOverrides[precompileAddr] = new CodeInfo(worldState.GetCode(precompileAddr));
        _overriddenPrecompileAddresses.Add(precompileAddr);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        _codeOverrides.TryGetValue(address, out CodeInfo? result)
            ? ICodeInfoRepository.TryGetDelegatedAddress(result.CodeSpan, out delegatedAddress)
            : codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);


    public void ResetOverrides()
    {
        _precompileOverrides.Clear();
        _codeOverrides.Clear();
        _overriddenPrecompileAddresses.Clear();
    }

    public void ResetPrecompileOverrides()
    {
        foreach (Address address in _overriddenPrecompileAddresses)
        {
            _codeOverrides.Remove(address);
        }
        _overriddenPrecompileAddresses.Clear();
        _precompileOverrides.Clear();
    }
}
