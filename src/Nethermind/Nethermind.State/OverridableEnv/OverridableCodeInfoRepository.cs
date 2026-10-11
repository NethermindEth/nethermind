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
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, CodeOverrideStore? overrides = null) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, (CodeInfo codeInfo, ValueHash256 codeHash)> _codeOverrides = (overrides ??= new CodeOverrideStore()).Code;
    private readonly Dictionary<Address, CodeInfo> _precompileOverrides = overrides.Precompiles;

    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        // Moved precompiles are rare, so skip the hash lookup when there are none.
        if (_precompileOverrides.Count != 0 && _precompileOverrides.TryGetValue(codeSource, out CodeInfo? precompile)) return precompile;

        if (TryGetCodeOverride(codeSource, vmSpec, out CodeInfo? result))
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
    /// A code override, including one at a precompile's address, runs as code. A precompile's address otherwise
    /// resolves to its code in the world state, as in geth, so code an earlier block's override left there still runs.
    /// A move destination resolves to its own code, not to the precompile moved there.
    /// </remarks>
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec) =>
        TryGetCodeOverride(target, vmSpec, out CodeInfo? result) ? result
        : vmSpec.IsPrecompile(target) ? new CodeInfo(worldState.GetCode(target))
        : codeInfoRepository.GetDelegatedCodeInfo(target, vmSpec);

    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        _precompileOverrides.TryGetValue(codeSource, out CodeInfo? precompile) ? precompile.Precompile
        : TryGetCodeOverride(codeSource, vmSpec, out CodeInfo? result) ? result.Precompile
        : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value) => _codeOverrides[key] = (value, worldState.GetCodeHash(key));

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = this.GetCachedCodeInfo(precompileAddr, vmSpec);
        _codeOverrides[precompileAddr] = (new CodeInfo(worldState.GetCode(precompileAddr)), worldState.GetCodeHash(precompileAddr));
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        TryGetCodeOverride(address, vmSpec, out CodeInfo? result)
            ? ICodeInfoRepository.TryGetDelegatedAddress(result.CodeSpan, out delegatedAddress)
            : codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);

    /// <summary>Finds the code this repository answers <paramref name="address"/> with, ahead of the inner repository.</summary>
    /// <remarks>
    /// An override answers while the account still has the code hash it had when the override was set, so any change
    /// of the account's code ends it: code written while the call runs, an authorization, and destroying the account
    /// (a SELFDESTRUCT before Cancun), which does not pass through this repository. The world state journals the code
    /// hash, so a reverted change brings the override back. At a precompile's address, which an override turned into
    /// an ordinary account, an ended override leaves the world state's code, not the precompile the inner repository
    /// would answer with.
    /// </remarks>
    private bool TryGetCodeOverride(Address address, IReleaseSpec vmSpec, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        if (_codeOverrides.TryGetValue(address, out (CodeInfo codeInfo, ValueHash256 codeHash) entry))
        {
            if (worldState.GetCodeHash(address) == entry.codeHash)
            {
                codeInfo = entry.codeInfo;
                return true;
            }

            if (vmSpec.IsPrecompile(address))
            {
                codeInfo = worldState.GetCodeHash(address) == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : new CodeInfo(worldState.GetCode(address));
                return true;
            }
        }

        codeInfo = null;
        return false;
    }

    public void ResetOverrides()
    {
        _precompileOverrides.Clear();
        _codeOverrides.Clear();
    }

    /// <remarks>
    /// Also drops the code overrides at <paramref name="spec"/>'s precompile addresses, moved-away origins included,
    /// so each block dispatches its precompiles again, even at an address a fork made a precompile since.
    /// </remarks>
    public void ResetPrecompileOverrides(IReleaseSpec spec)
    {
        foreach (Address address in _codeOverrides.Keys)
        {
            if (spec.IsPrecompile(address)) _codeOverrides.Remove(address);
        }
        _precompileOverrides.Clear();
    }
}
