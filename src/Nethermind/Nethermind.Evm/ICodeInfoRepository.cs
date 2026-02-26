// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;

namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, in SpecSnapshot spec, out Address? delegationAddress);
    bool TryGetDelegation(Address address, in SpecSnapshot spec, [NotNullWhen(true)] out Address? delegatedAddress);

    ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec);
    void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
    void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec);

    /// <remarks>
    /// Parses delegation code to extract the contained address.
    /// <b>Assumes </b><paramref name="code"/> <b>is delegation code!</b>
    /// </remarks>
    static bool TryGetDelegatedAddress(ReadOnlySpan<byte> code, [NotNullWhen(true)] out Address? address)
    {
        if (Eip7702Constants.IsDelegatedCode(code))
        {
            address = new Address(code[Eip7702Constants.DelegationHeader.Length..].ToArray());
            return true;
        }

        address = null;
        return false;
    }
}

public static class CodeInfoRepositoryExtensions
{
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, in SpecSnapshot vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, vmSpec, out _);
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, in SpecSnapshot vmSpec, out Address? delegationAddress)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, true, vmSpec, out delegationAddress);

    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, vmSpec, out _);
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, true, vmSpec, out delegationAddress);
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        SpecSnapshot snapshot = vmSpec.GetSnapshot();
        return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, in snapshot, out delegationAddress);
    }

    public static bool TryGetDelegation(this ICodeInfoRepository codeInfoRepository, Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress)
    {
        SpecSnapshot snapshot = spec.GetSnapshot();
        return codeInfoRepository.TryGetDelegation(address, in snapshot, out delegatedAddress);
    }
}
