// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

public class OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository, IWorldState worldState) : IOverridableCodeInfoRepository
{
    private readonly Dictionary<Address, (CodeInfo codeInfo, ValueHash256 codeHash)> _codeOverrides = [];
    private readonly Dictionary<Address, (CodeInfo codeInfo, Address initialAddr)> _precompileOverrides = [];

    public bool IsCodeOverridable => true;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        // Moved precompiles are rare, so skip the hash lookup when there are none.
        if (_precompileOverrides.Count != 0 && _precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile)) return precompile.codeInfo;

        if (TryGetCodeOverride(codeSource, out CodeInfo? result))
        {
            return !result.IsEmpty &&
                   ICodeInfoRepository.TryGetDelegatedAddress(result.CodeSpan, out delegationAddress) &&
                   followDelegation
                ? GetCachedCodeInfo(delegationAddress, false, vmSpec, out Address? _)
                : result;
        }

        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
    }

    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        _precompileOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, Address initialAddr) precompile) ? precompile.codeInfo.Precompile
        : _codeOverrides.TryGetValue(codeSource, out (CodeInfo codeInfo, ValueHash256 codeHash) result) ? result.codeInfo.Precompile
        : codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        codeInfoRepository.InsertCode(code, codeOwner, spec);

    public void SetCodeOverride(
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value)
    {
        ValueHash256 codeHash = worldState.GetCodeHash(key);
        Debug.Assert(value.IsPrecompile || ValueKeccak.Compute(value.CodeSpan) == codeHash,
            $"The world state must hold the override code at {key} before {nameof(SetCodeOverride)}");
        _codeOverrides[key] = (value, codeHash);
    }

    public void MovePrecompile(IReleaseSpec vmSpec, Address precompileAddr, Address targetAddr)
    {
        _precompileOverrides[targetAddr] = (this.GetCachedCodeInfo(precompileAddr, vmSpec), precompileAddr);
        _codeOverrides[precompileAddr] = (new CodeInfo(worldState.GetCode(precompileAddr)), worldState.GetCodeHash(precompileAddr));
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        codeInfoRepository.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec vmSpec,
        [NotNullWhen(true)] out Address? delegatedAddress) =>
        TryGetCodeOverride(address, out CodeInfo? result)
            ? ICodeInfoRepository.TryGetDelegatedAddress(result.CodeSpan, out delegatedAddress)
            : codeInfoRepository.TryGetDelegation(address, vmSpec, out delegatedAddress);

    /// <summary>Resolves the code override for <paramref name="address"/>, if one was set.</summary>
    /// <remarks>
    /// SETCODEFROM (EIP-8298) and EIP-7702 delegations write code to the world state directly, so an override is
    /// served only while the state still holds the code hash it was set against. Once it does not, the address
    /// serves the code in state instead, still as an override so an overridden precompile stays shadowed. A reverted
    /// write restores the hash, and with it the override's code, which state overrides also write to the state.
    /// </remarks>
    private bool TryGetCodeOverride(Address address, [NotNullWhen(true)] out CodeInfo? codeInfo)
    {
        ref (CodeInfo codeInfo, ValueHash256 codeHash) entry = ref CollectionsMarshal.GetValueRefOrNullRef(_codeOverrides, address);
        if (Unsafe.IsNullRef(ref entry))
        {
            codeInfo = null;
            return false;
        }

        ValueHash256 codeHash = worldState.GetCodeHash(address);
        if (entry.codeHash != codeHash)
        {
            entry = (codeHash == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : CodeInfoRepository.GetCodeInfo(worldState, address, in codeHash), codeHash);
        }

        codeInfo = entry.codeInfo;
        return true;
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
