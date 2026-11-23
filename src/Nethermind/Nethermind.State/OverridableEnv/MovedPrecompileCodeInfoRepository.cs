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
    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        if (TryGetMoved(codeSource, out CodeInfo? codeInfo, withCurrentCode: true) || TryGetOverriddenPrecompileAddress(codeSource, vmSpec, out codeInfo))
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

    /// <inheritdoc/>
    /// <remarks>
    /// Answers from the env's store alone. A code change after an override keeps a retained native precompile and
    /// leaves other overrides without one, so the account's current code hash cannot change the answer.
    /// </remarks>
    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        TryGetMoved(codeSource, out CodeInfo? codeInfo) ? codeInfo.Precompile
        : TryGetPrecompileCodeOverride(codeSource, vmSpec, out (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) entry) ? entry.codeInfo.Precompile
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

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void InsertTransientCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertTransientCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

    /// <summary>Returns the precompile moved to <paramref name="codeSource"/>.</summary>
    /// <remarks>
    /// The destination is an ordinary account whose code an earlier transaction or an EIP-7702 authorization can change
    /// after the move. With <paramref name="withCurrentCode"/>, a destination whose code hash no longer matches the one
    /// recorded at the move gets a view of its current code. The env's store is shared, so that view is not written back.
    /// </remarks>
    private bool TryGetMoved(Address codeSource, [NotNullWhen(true)] out CodeInfo? codeInfo, bool withCurrentCode = false)
    {
        Dictionary<Address, (CodeInfo codeInfo, ValueHash256 codeHash)> precompiles = overrides.Precompiles;
        if (precompiles.Count == 0 || !precompiles.TryGetValue(codeSource, out (CodeInfo codeInfo, ValueHash256 codeHash) entry))
        {
            codeInfo = null;
            return false;
        }

        codeInfo = withCurrentCode && worldState.GetCodeHash(codeSource) != entry.codeHash
            ? new CodeInfo(entry.codeInfo.Precompile!, worldState.GetCode(codeSource))
            : entry.codeInfo;
        return true;
    }

    /// <summary>Returns the env's dispatch or plain-code view of an overridden precompile, including a moved-away origin.</summary>
    /// <remarks>
    /// The account's code can change after the override, for example through an EIP-7702 authorization of a
    /// dynamically registered native address. As in <see cref="OverridableCodeInfoRepository"/>, a view whose recorded
    /// code hash no longer matches is rebuilt from the world state, keeping a retained native precompile. The env's
    /// store is shared, so that view is not written back.
    /// </remarks>
    private bool TryGetOverriddenPrecompileAddress(Address codeSource, IReleaseSpec vmSpec, [NotNullWhen(true)] out CodeInfo? codeInfo, bool forDelegation = false)
    {
        if (TryGetPrecompileCodeOverride(codeSource, vmSpec, out (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) entry))
        {
            ValueHash256 codeHash = worldState.GetCodeHash(codeSource);
            if (codeHash == entry.codeHash)
            {
                codeInfo = forDelegation ? entry.delegatedCodeInfo : entry.codeInfo;
                return true;
            }

            CodeInfo visibleCode = codeHash == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : new CodeInfo(worldState.GetCode(codeSource));
            codeInfo = forDelegation || !entry.codeInfo.IsPrecompile ? visibleCode : new CodeInfo(entry.codeInfo.Precompile!, visibleCode.Code);
            return true;
        }

        codeInfo = null;
        return false;
    }

    /// <summary>Finds the env's code override at a precompile address, a moved-away origin or a retained native address.</summary>
    private bool TryGetPrecompileCodeOverride(Address codeSource, IReleaseSpec vmSpec,
        out (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash) entry)
    {
        if (overrides.Code.Count != 0 && overrides.Code.TryGetValue(codeSource, out entry) &&
            (vmSpec.IsPrecompile(codeSource) || entry.codeInfo.IsPrecompile))
        {
            return true;
        }

        entry = default;
        return false;
    }
}
