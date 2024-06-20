// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;
using System;
using System.Collections.Generic;

namespace Nethermind.Evm;
public class AuthorizedCodeInfoRepository(ICodeInfoRepository codeInfoRepository) : ICodeInfoRepository
{
    public IEnumerable<Address> AuthorizedAddresses => _authorizedCode.Keys;
    private readonly Dictionary<Address, CodeInfo> _authorizedCode = new();

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _authorizedCode.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);

    public CodeInfo GetOrAdd(ValueHash256 codeHash, ReadOnlySpan<byte> initCode) => codeInfoRepository.GetOrAdd(codeHash, initCode);

    public void InsertCode(IWorldState state, ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec) =>
        throw new NotSupportedException($"Use {nameof(CopyCodeAndOverwrite)}() for code authorizations.");

    /// <summary>
    /// Copy code from <paramref name="codeSource"/> and set it to override <paramref name="target"/>.
    /// Main use for this is for https://eips.ethereum.org/EIPS/eip-7702
    /// </summary>
    /// <param name="code"></param>
    public void CopyCodeAndOverwrite(
        IWorldState worldState,
        Address codeSource,
        Address target,
        IReleaseSpec vmSpec)
    {
        if (!_authorizedCode.ContainsKey(target))
        {
            _authorizedCode.Add(target, GetCachedCodeInfo(worldState, codeSource, vmSpec));
        }
    }

    public void ClearAuthorizations()
    {
        _authorizedCode.Clear();
    }
}
