// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    /// <summary>Whether account code may be overridden (e.g. <c>eth_call</c> state overrides), disabling the simple-transfer fast path.</summary>
    /// <remarks>Wrapping implementations must forward this, else the fast path is wrongly taken under overrides.</remarks>
    bool IsCodeOverridable { get; }
    CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress);
    void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
    void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec);
    bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress);

    /// <remarks>
    /// Parses delegation code to extract the contained address.
    /// <b>Assumes </b><paramref name="code"/> <b>is delegation code!</b>
    /// </remarks>
    static bool TryGetDelegatedAddress(ReadOnlySpan<byte> code, [NotNullWhen(true)] out Address? address)
    {
        if (Eip7702Constants.IsDelegatedCode(code))
        {
            address = new Address(code[Eip7702Constants.DelegationHeader.Length..]);
            return true;
        }

        address = null;
        return false;
    }

}

public static class CodeInfoRepositoryExtensions
{
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, vmSpec, out _);
    public static CodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, true, vmSpec, out delegationAddress);

    /// <summary>
    /// Returns the <see cref="CodeInfo"/> at <paramref name="codeSource"/> without resolving any EIP-7702 delegation.
    /// </summary>
    public static CodeInfo GetCachedCodeInfoNoDelegation(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, false, vmSpec, out _);
}
