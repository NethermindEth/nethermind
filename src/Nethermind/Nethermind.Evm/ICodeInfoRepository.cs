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
    ICodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress);
    ValueHash256 GetExecutableCodeHash(Address address, IReleaseSpec spec);
    void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
    void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec);
    bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress);
}

public static class CodeInfoRepositoryExtensions
{
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, vmSpec, out _);
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
        => codeInfoRepository.GetCachedCodeInfo(codeSource, true, vmSpec, out delegationAddress);
}
