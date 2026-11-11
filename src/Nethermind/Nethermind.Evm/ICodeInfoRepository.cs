// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
namespace Nethermind.Evm;

/// <remarks>
/// Extension contract: there is no base class to derive from, so an added member is a compile error for every
/// implementation, deliberately. A wrapping repository has to answer for the one it wraps, and a default body
/// would let a missing forward return the terminal answer silently — the reason <see cref="IsCodeOverridable"/>
/// had its <c>=> false</c> default stripped again in #12282.
/// <para>The writing members, <see cref="InsertCode"/>, <see cref="InsertTransientCode"/> and <see cref="SetDelegation"/>,
/// take the world state to write to instead of using the one the repository reads from, so a caller that records
/// a transaction into a wrapper of that state, as the EIP-7906 ASSERT diff does, sees the writes too. That state
/// must be the one the repository reads from, or a write-through wrapper of it: reads keep going through the
/// repository's own state, so a write to an unrelated state would leave the two disagreeing.</para>
/// </remarks>
public interface ICodeInfoRepository
{
    /// <summary>Whether account code may be overridden (e.g. <c>eth_call</c> state overrides), disabling the simple-transfer fast path.</summary>
    /// <remarks>Wrapping implementations must forward this, else the fast path is wrongly taken under overrides.</remarks>
    bool IsCodeOverridable { get; }
    CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress);

    /// <summary>Resolves the precompile at <paramref name="codeSource"/>, or null when <paramref name="vmSpec"/> enables none there.</summary>
    /// <remarks>Records no account access, so unlike <see cref="GetCachedCodeInfo"/> it creates no EIP-7928 entry.</remarks>
    IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec);

    /// <summary>Resolves the code an EIP-7702 delegation to <paramref name="target"/> executes.</summary>
    /// <remarks>
    /// Empty for a precompile, which must not execute via delegation. Callers ask this rather than
    /// <see cref="GetCachedCodeInfo"/>, which cannot tell the lookup from a direct call, so a repository that
    /// places precompiles at other addresses (a state override moving one) can keep them from running here.
    /// </remarks>
    CodeInfo GetDelegatedCodeInfo(Address target, IReleaseSpec vmSpec);

    /// <summary>Deposits <paramref name="code"/> into <paramref name="worldState"/> and primes any cache the repository keeps.</summary>
    void InsertCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);

    /// <summary>Installs EIP-8360 <c>TCREATE</c> code, which the world state never writes to the code database.</summary>
    /// <remarks>Goes through the repository, as <see cref="InsertCode"/> does, so a wrapper sees the code change.</remarks>
    void InsertTransientCode(IWorldState worldState, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);

    /// <summary>Sets the EIP-7702 delegation of <paramref name="authority"/> to <paramref name="codeSource"/> in
    /// <paramref name="worldState"/>, or clears it for <see cref="Address.Zero"/>, and primes any cache the repository keeps.</summary>
    void SetDelegation(IWorldState worldState, Address codeSource, Address authority, IReleaseSpec spec);
    bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress);

    /// <remarks>
    /// Parses delegation code to extract the contained address.
    /// <b>Assumes </b><paramref name="code"/> <b>is delegation code!</b>
    /// </remarks>
    static bool TryGetDelegatedAddress(ReadOnlySpan<byte> code, [NotNullWhen(true)] out Address? address)
    {
        if (Eip7702Constants.IsDelegatedCode(code))
        {
            address = new Address(code[Eip7702Constants.DelegationHeaderLength..]);
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
    [SkipLocalsInit]
    public static CodeInfo GetCachedCodeInfoNoDelegation(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, false, vmSpec, out _);
}
