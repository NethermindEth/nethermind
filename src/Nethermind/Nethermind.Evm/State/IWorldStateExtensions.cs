// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.State;

public static class WorldStateExtensions
{
    public static void InsertCode(this IWorldState worldState, Address address, ReadOnlyMemory<byte> code,
        IReleaseSpec spec, bool isGenesis = false)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        worldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }
}
