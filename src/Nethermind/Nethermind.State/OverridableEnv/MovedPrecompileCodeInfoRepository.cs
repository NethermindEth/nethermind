// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

/// <summary>Applies the precompile overrides of an overridable env over a per-transaction code repository.</summary>
/// <remarks>
/// Block access list execution gives each transaction its own code repository. Code overrides are already in the
/// world state, so only what changes precompile dispatch, which lives in the env's <see cref="CodeOverrideStore"/>,
/// needs applying: a moved precompile runs at its destination, and an overridden precompile address runs its code
/// unless its env retained native dispatch. Both retain the account's visible code and plain delegation view.
/// Every other lookup reads through <paramref name="codeInfoRepository"/>, which sees later state changes such as
/// EIP-7702 delegations.
/// </remarks>
public class MovedPrecompileCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, CodeOverrideStore overrides) : ICodeInfoRepository
{
    /// <summary>Views of overridden addresses whose code changed after the override, kept for this transaction only.</summary>
    private Dictionary<Address, (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash)>? _refreshed;

    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        if (TryGetMoved(codeSource, out CodeInfo? codeInfo) || TryGetOverriddenPrecompileAddress(codeSource, vmSpec, out codeInfo))
        {
            worldState.AddAccountRead(codeSource);
            worldState.RecordAccountAccess(codeSource);
            if (vmSpec.IsEip7702Enabled && !codeInfo.IsPrecompile && !codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
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
        : TryGetOverriddenPrecompileAddress(codeSource, vmSpec, out codeInfo) ? codeInfo.Precompile
        : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    /// <inheritdoc/>
    /// <remarks>
    /// Every target, a precompile's address and a move destination included, resolves to its code in the world state,
    /// as in geth, so code an override left at a precompile's address runs, in its own block and in later ones.
    /// </remarks>
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec) =>
        TryGetOverriddenPrecompileAddress(target, vmSpec, out CodeInfo? result, forDelegation: true) ? result
        : vmSpec.IsPrecompile(target)
            ? new CodeInfo(worldState.GetCode(target))
            : codeInfoRepository.GetDelegatedCodeInfo(target, vmSpec);

    public void InsertCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(worldState, code, codeOwner, spec);

    public void InsertTransientCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertTransientCode(worldState, code, codeOwner, spec);

    public void SetDelegation(IWorldState worldState, Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(worldState, codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

    /// <summary>Returns the precompile moved to <paramref name="codeSource"/>.</summary>
    private bool TryGetMoved(Address codeSource, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        Dictionary<Address, CodeInfo> precompiles = overrides.Precompiles;
        codeInfo = null;
        return precompiles.Count != 0 && precompiles.TryGetValue(codeSource, out codeInfo);
    }

    /// <summary>Returns the env's dispatch or plain-code view of an overridden precompile, including a moved-away origin.</summary>
    /// <remarks>
    /// The env's views hold the code the override set. When the account's code has changed since, for example by an
    /// EIP-7702 authorization at an address a plugin dispatches natively, both views are rebuilt from the world state,
    /// as <see cref="OverridableCodeInfoRepository"/> does: a retained native dispatch keeps its precompile with the new
    /// visible code, and any other override is replaced by that code. The rebuilt views stay in this repository and
    /// leave the env's shared store as it is.
    /// </remarks>
    private bool TryGetOverriddenPrecompileAddress(Address codeSource, IReleaseSpec vmSpec, [NotNullWhen(true)] out CodeInfo? codeInfo, bool forDelegation = false)
    {
        if (overrides.Code.Count != 0 && overrides.Code.TryGetValue(codeSource, out (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) entry) &&
            (vmSpec.IsPrecompile(codeSource) || entry.codeInfo.IsPrecompile))
        {
            ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
            if (codeHash != entry.codeHash)
                entry = GetRefreshedViews(codeSource, entry.codeInfo.Precompile, codeHash);

            codeInfo = forDelegation ? entry.delegatedCodeInfo : entry.codeInfo;
            return true;
        }

        codeInfo = null;
        return false;
    }

    private (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) GetRefreshedViews(Address codeSource, IPrecompile? precompile, in ValueHash256 codeHash)
    {
        _refreshed ??= [];
        if (_refreshed.TryGetValue(codeSource, out (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) refreshed) && refreshed.codeHash == codeHash)
            return refreshed;

        CodeInfo delegatedCodeInfo = codeHash == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : new CodeInfo(worldState.GetCode(codeSource));
        CodeInfo dispatchCodeInfo = precompile is null ? delegatedCodeInfo : new CodeInfo(precompile, delegatedCodeInfo.Code);
        return _refreshed[codeSource] = (dispatchCodeInfo, delegatedCodeInfo, codeHash);
    }
}
