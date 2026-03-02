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
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _localPrecompiles;
    private readonly IWorldState _worldState;
    private readonly Func<ValueHash256, IReleaseSpec, CodeInfo> _codeInfoLoader;
    private readonly IBlockAccessListBuilder? _balBuilder;

    public CodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
        : this(worldState, precompileProvider, codeInfoLoader: null)
    {
    }

    internal CodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider, Func<ValueHash256, IReleaseSpec, CodeInfo>? codeInfoLoader)
    {
        _localPrecompiles = precompileProvider.GetPrecompiles();
        _worldState = worldState;
        _balBuilder = _worldState as IBlockAccessListBuilder;
        _codeInfoLoader = codeInfoLoader ?? DefaultLoad;

        CodeInfo DefaultLoad(ValueHash256 codeHash, IReleaseSpec spec) =>
            codeHash == ValueKeccak.OfAnEmptyString ? CodeInfo.Empty : GetCodeInfo(worldState, in codeHash, spec);
    }

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;
        if (vmSpec.IsPrecompile(codeSource))
        {
            if (_balBuilder is not null && _balBuilder.TracingEnabled)
            {
                _balBuilder.AddAccountRead(codeSource);
            }
            return _localPrecompiles[codeSource];
        }

        CodeInfo codeInfo = InternalGetCodeInfo(codeSource, vmSpec);

        if (!codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
        {
            if (followDelegation)
            {
                codeInfo = InternalGetCodeInfo(delegationAddress, vmSpec);
            }
        }

        return codeInfo;
    }

    private CodeInfo InternalGetCodeInfo(Address codeSource, IReleaseSpec vmSpec)
    {
        ref readonly ValueHash256 codeHash = ref _worldState.GetCodeHash(codeSource);
        return _codeInfoLoader(codeHash, vmSpec);
    }

    internal static CodeInfo GetCodeInfo(IWorldState worldState, in ValueHash256 codeHash, IReleaseSpec vmSpec)
    {
        byte[]? code = worldState.GetCode(in codeHash);
        if (code is null)
        {
            MissingCode(in codeHash);
        }

        return CodeInfoFactory.CreateCodeInfo(code, vmSpec);

        [DoesNotReturn, StackTraceHidden]
        static void MissingCode(in ValueHash256 codeHash) => throw new DataException($"Code {codeHash} missing in the state");
    }


    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        InsertCode(_worldState, code, codeOwner, spec, out _);

    public static bool InsertCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec, out ValueHash256 codeHash)
    {
        codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        return worldState.InsertCode(codeOwner, in codeHash, code, spec);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec) =>
        SetDelegation(_worldState, codeSource, authority, spec, out _, out _);

    public static bool SetDelegation(
        IWorldState worldState,
        Address codeSource,
        Address authority,
        IReleaseSpec spec,
        out ValueHash256 codeHash,
        out byte[] authorizedBuffer)
    {
        if (codeSource != Address.Zero)
        {
            authorizedBuffer = new byte[Eip7702Constants.DelegationHeader.Length + Address.Size];
            Eip7702Constants.DelegationHeader.CopyTo(authorizedBuffer);
            codeSource.Bytes.CopyTo(authorizedBuffer, Eip7702Constants.DelegationHeader.Length);
            codeHash = ValueKeccak.Compute(authorizedBuffer);
        }
        else
        {
            authorizedBuffer = Array.Empty<byte>();
            codeHash = ValueKeccak.OfAnEmptyString;
        }

        return worldState.InsertCode(authority, codeHash, authorizedBuffer, spec);
    }

    /// <summary>
    /// Retrieves code hash of delegation if delegated. Otherwise, code hash of <paramref name="address"/>.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="spec"></param>
    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec)
    {
        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            return Keccak.OfAnEmptyString.ValueHash256;
        }

        CodeInfo codeInfo = _codeInfoLoader(codeHash, spec);
        return codeInfo.IsEmpty
            ? Keccak.OfAnEmptyString.ValueHash256
            : codeHash;
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        ICodeInfoRepository.TryGetDelegatedAddress(InternalGetCodeInfo(address, spec).CodeSpan, out delegatedAddress);
}
