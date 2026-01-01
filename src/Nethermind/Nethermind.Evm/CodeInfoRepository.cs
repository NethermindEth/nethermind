// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <remarks>
/// Implementation without any cache so that it always goes through
/// the world state, which captures touched bytecodes.
/// Relevant for witness generation (and therefore stateless reprocessing).
/// </remarks>
public class CodeInfoRepository : ICodeInfoRepository
{
    private readonly FrozenDictionary<AddressAsKey, PrecompileInfo> _localPrecompiles;
    protected readonly IWorldState _worldState;

    public CodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
    {
        _localPrecompiles = precompileProvider.GetPrecompiles();
        _worldState = worldState;
    }

    public ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (vmSpec.IsPrecompile(codeSource)) // _localPrecompiles have to have all precompiles
        {
            return _localPrecompiles[codeSource];
        }

        ICodeInfo codeInfo = InternalGetCodeInfo(codeSource, vmSpec);

        if (!codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
        {
            if (followDelegation)
                codeInfo = InternalGetCodeInfo(delegationAddress, vmSpec);
        }

        return codeInfo;
    }

    private ICodeInfo InternalGetCodeInfo(Address codeSource, IReleaseSpec vmSpec)
    {
        ref readonly ValueHash256 codeHash = ref _worldState.GetCodeHash(codeSource);
        return InternalGetCodeInfo(in codeHash, vmSpec);
    }

    protected virtual ICodeInfo InternalGetCodeInfo(in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            return CodeInfo.Empty;
        }

        byte[]? code = _worldState.GetCode(in codeHash);
        if (code is null)
        {
            MissingCode(in codeHash);
        }

        return CodeInfoFactory.CreateCodeInfo(code, vmSpec, ValidationStrategy.ExtractHeader);

        [DoesNotReturn, StackTraceHidden]
        static void MissingCode(in ValueHash256 codeHash)
        {
            throw new DataException($"Code {codeHash} missing in the state");
        }
    }

    public virtual void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        _worldState.InsertCode(codeOwner, in codeHash, code, spec);
    }

    public virtual void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        if (codeSource == Address.Zero)
        {
            _worldState.InsertCode(authority, Keccak.OfAnEmptyString, Array.Empty<byte>(), spec);
            return;
        }

        byte[] authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
        Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
        codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
        ValueHash256 codeHash = ValueKeccak.Compute(authorizedBuffer);
        _worldState.InsertCode(authority, codeHash, authorizedBuffer.AsMemory(), spec);
    }

    /// <summary>
    /// Retrieves code hash of delegation if delegated. Otherwise code hash of <paramref name="address"/>.
    /// </summary>
    /// <param name="worldState"></param>
    /// <param name="address"></param>
    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec)
    {
        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            return Keccak.OfAnEmptyString.ValueHash256;
        }

        ICodeInfo codeInfo = InternalGetCodeInfo(in codeHash, spec);
        return codeInfo.IsEmpty
            ? Keccak.OfAnEmptyString.ValueHash256
            : codeHash;
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec, out Address? delegatedAddress) =>
        ICodeInfoRepository.TryGetDelegatedAddress(InternalGetCodeInfo(address, spec).CodeSpan, out delegatedAddress);
}
