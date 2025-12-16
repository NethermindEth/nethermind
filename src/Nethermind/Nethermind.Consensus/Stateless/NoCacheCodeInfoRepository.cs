using System;
using System.Collections.Frozen;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Same implementation as CodeInfoRepository but without cache so that it always goes through
/// the world state, which captures touched bytecodes.
/// </summary>
public sealed class NoCacheCodeInfoRepository : ICodeInfoRepository
{
    private readonly FrozenDictionary<AddressAsKey, PrecompileInfo> _localPrecompiles;
    private readonly IWorldState _worldState;

    public NoCacheCodeInfoRepository(IWorldState worldState, IPrecompileProvider precompileProvider)
    {
        _localPrecompiles = precompileProvider.GetPrecompiles();
        _worldState = worldState;
    }

    public ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        delegationAddress = null;

        if (vmSpec.IsPrecompile(codeSource))
        {
            return _localPrecompiles[codeSource];
        }

        ICodeInfo codeInfo = GetCodeInfo(codeSource, vmSpec);

        if (!codeInfo.IsEmpty && ICodeInfoRepository.TryGetDelegatedAddress(codeInfo.CodeSpan, out delegationAddress))
        {
            if (followDelegation)
                codeInfo = GetCodeInfo(delegationAddress, vmSpec);
        }

        return codeInfo;
    }

    private ICodeInfo GetCodeInfo(Address codeSource, IReleaseSpec vmSpec)
    {
        ref readonly ValueHash256 codeHash = ref _worldState.GetCodeHash(codeSource);
        return GetCodeInfo(in codeHash, vmSpec);
    }

    private ICodeInfo GetCodeInfo(in ValueHash256 codeHash, IReleaseSpec vmSpec)
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

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        _worldState.InsertCode(codeOwner, in codeHash, code, spec);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
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

    public ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec)
    {
        ValueHash256 codeHash = _worldState.GetCodeHash(address);
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256)
        {
            return Keccak.OfAnEmptyString.ValueHash256;
        }

        ICodeInfo codeInfo = GetCodeInfo(in codeHash, spec);
        return codeInfo.IsEmpty
            ? Keccak.OfAnEmptyString.ValueHash256
            : codeHash;
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec, out Address? delegatedAddress) =>
        ICodeInfoRepository.TryGetDelegatedAddress(GetCodeInfo(address, spec).CodeSpan, out delegatedAddress);
}
