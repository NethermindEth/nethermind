// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress);

    /// <summary>
    /// Loads account code reached through EIP-7702 delegation, bypassing native precompile dispatch.
    /// </summary>
    /// <param name="delegationAddress">The address encoded in delegation code.</param>
    /// <returns>The code info for the delegated account.</returns>
    CodeInfo GetDelegatedCodeInfo(Address delegationAddress);

    void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
    void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec);

    /// <summary>
    /// Returns whether account code at <paramref name="address"/> is EIP-7702 delegation code.
    /// </summary>
    /// <param name="address">The account address to inspect.</param>
    /// <returns><c>true</c> when the account has delegation code; otherwise <c>false</c>.</returns>
    bool HasDelegation(Address address);
}

public static class CodeInfoRepositoryExtensions
{
    extension(ICodeInfoRepository codeInfoRepository)
    {
        public CodeInfo GetCachedCodeInfo(Address codeSource, IReleaseSpec vmSpec)
            => codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation: true, vmSpec, out _);
    }
}
