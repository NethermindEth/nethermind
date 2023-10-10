// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.State;

namespace Nethermind.Facade;

public class OverridableCodeInfoRepository : ICodeInfoRepository
{
    private readonly ICodeInfoRepository _codeInfoRepository;
    private readonly Dictionary<Address, CodeInfo> _codeOverwrites = new();

    public OverridableCodeInfoRepository(ICodeInfoRepository codeInfoRepository)
    {
        _codeInfoRepository = codeInfoRepository;
    }

    public void SetCodeOverwrite(
        IWorldState worldState,
        IReleaseSpec vmSpec,
        Address key,
        CodeInfo value,
        Address? redirectAddress = null)
    {
        if (redirectAddress is not null)
        {
            _codeOverwrites[redirectAddress] = GetCachedCodeInfo(worldState, key, vmSpec);
        }

        _codeOverwrites[key] = value;
    }

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec) =>
        _codeOverwrites.TryGetValue(codeSource, out CodeInfo result)
            ? result
            : _codeInfoRepository.GetCachedCodeInfo(worldState, codeSource, vmSpec);

    public CodeInfo GetOrAdd(ValueKeccak codeHash, Span<byte> initCode) =>
        _codeInfoRepository.GetOrAdd(codeHash, initCode);
}
