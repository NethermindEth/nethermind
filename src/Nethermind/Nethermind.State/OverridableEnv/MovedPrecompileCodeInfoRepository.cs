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

/// <summary>Applies the precompile moves of an overridable env over a per-transaction code repository.</summary>
/// <remarks>
/// Block access list execution gives each transaction its own code repository. Code overrides are already in the
/// world state, so only the precompile moves, which live in the env's <see cref="CodeOverrideStore"/>, need applying.
/// Every other lookup reads through <paramref name="codeInfoRepository"/>, which sees later state changes such as
/// EIP-7702 delegations.
/// </remarks>
public class MovedPrecompileCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState, CodeOverrideStore overrides) : ICodeInfoRepository
{
    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        if (TryGetMoved(codeSource, out CodeInfo? codeInfo))
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
        TryGetMoved(codeSource, out CodeInfo? codeInfo) ? codeInfo.Precompile : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    /// <inheritdoc/>
    /// <remarks>
    /// A code override at a precompile's address runs as code, as in <see cref="OverridableCodeInfoRepository"/>;
    /// no transaction can change the code there, so the env's copy is current. Any other target, a move destination
    /// included, resolves to its code in the world state.
    /// </remarks>
    public CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec) =>
        vmSpec.IsPrecompile(target) && overrides.Code.TryGetValue(target, out CodeInfo? codeInfo)
            ? codeInfo
            : codeInfoRepository.GetDelegatedCodeInfo(target, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);

    /// <summary>Returns the code of a move destination, or of a move origin, which is no longer a precompile.</summary>
    private bool TryGetMoved(Address codeSource, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        Dictionary<Address, (CodeInfo codeInfo, Address initialAddr)> precompiles = overrides.Precompiles;
        if (precompiles.Count == 0)
        {
            codeInfo = null;
            return false;
        }

        if (precompiles.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) moved))
        {
            codeInfo = moved.codeInfo;
            return true;
        }

        foreach ((CodeInfo _, Address initialAddr) in precompiles.Values)
        {
            if (initialAddr == codeSource)
            {
                codeInfo = overrides.Code[codeSource];
                return true;
            }
        }

        codeInfo = null;
        return false;
    }
}
