// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress, int? blockAccessIndex = null);
    // ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec);
    void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec, int? blockAccessIndex = null);
    void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec, int? blockAccessIndex = null);
    bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress, int? blockAccessIndex = null);

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
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec, int? blockAccessIndex = null)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, vmSpec, out _, blockAccessIndex);
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress, int? blockAccessIndex = null)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, true, vmSpec, out delegationAddress, blockAccessIndex);
}
