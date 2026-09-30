// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

/// <summary>Applies the precompile overrides of an overridable env over a per-transaction code repository.</summary>
/// <remarks>
/// Block access list execution gives each transaction its own code repository. Code overrides are already in the
/// world state, so only what changes precompile dispatch, which lives in the env's <see cref="CodeOverrideStore"/>,
/// needs applying: a moved precompile runs at its destination, and an overridden precompile address runs its code.
/// Every other lookup reads through <paramref name="codeInfoRepository"/>, which sees later state changes such as
/// EIP-7702 delegations.
/// </remarks>
public class MovedPrecompileCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, CodeOverrideStore overrides) : ICodeInfoRepository
{
    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        if (TryGetMoved(codeSource, out CodeInfo? codeInfo) || TryGetOverriddenPrecompileAddress(codeSource, vmSpec, out codeInfo))
        {
            worldState.AddAccountRead(codeSource);
            worldState.RecordAccountAccess(codeSource);
            if (!codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
            {
                return followDelegation ? GetDelegatedCodeInfo(delegationAddress, vmSpec) : codeInfo;
            }

            delegationAddress = null;
            return codeInfo;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        TryGetMoved(codeSource, out CodeInfo? codeInfo) ? codeInfo.Precompile
        : TryGetOverriddenPrecompileAddress(codeSource, vmSpec, out _) ? null
        : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    /// <inheritdoc/>
    /// <remarks>
    /// Every target, a precompile's address and a move destination included, resolves to its code in the world state,
    /// as in geth, so code an override left at a precompile's address runs, in its own block and in later ones.
    /// </remarks>
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec) =>
        vmSpec.IsPrecompile(target)
            ? new CodeInfo(worldState.GetCode(target))
            : codeInfoRepository.GetDelegatedCodeInfo(target, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

    /// <summary>Returns the precompile moved to <paramref name="codeSource"/>.</summary>
    private bool TryGetMoved(Address codeSource, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        Dictionary<Address, CodeInfo> precompiles = overrides.Precompiles;
        codeInfo = null;
        return precompiles.Count != 0 && precompiles.TryGetValue(codeSource, out codeInfo);
    }

    /// <summary>Returns the code of a precompile address that an override, a move away included, turned into an account.</summary>
    /// <remarks>No transaction can change the code at a precompile address, so the env's copy is current.</remarks>
    private bool TryGetOverriddenPrecompileAddress(Address codeSource, IReleaseSpec vmSpec, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        codeInfo = null;
        return vmSpec.IsPrecompile(codeSource) && overrides.Code.TryGetValue(codeSource, out codeInfo);
    }
}
