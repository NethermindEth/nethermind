// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Evm;

public interface ICodeInfoRepository
{
    ICodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress);
    ValueHash256 GetExecutableCodeHash(IWorldState worldState, Address address, IReleaseSpec spec);
    void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec);
    void SetDelegation(IWorldState state, Address codeSource, Address authority, IReleaseSpec spec);
    bool TryGetDelegation(IReadOnlyStateProvider worldState, Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress);
    bool TryGetDelegation(IReadOnlyStateProvider worldState, in ValueHash256 codeHash, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress);
}

public static class CodeInfoRepositoryExtensions
{
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
        => codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec, out _);
    public static ICodeInfo GetCachedCodeInfo(this ICodeInfoRepository codeInfoRepository, IWorldState worldState, Address codeSource, IReleaseSpec vmSpec, out Address? delegationAddress)
        => codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, true, vmSpec, out delegationAddress);
}
