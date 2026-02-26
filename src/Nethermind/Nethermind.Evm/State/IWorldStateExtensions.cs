// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public static class WorldStateExtensions
{
    public static void InsertCode(this IWorldState worldState, Address address, ReadOnlyMemory<byte> code,
        IReleaseSpec spec, bool isGenesis = false)
    {
        ValueHash256 codeHash = code.Length == 0 ? ValueKeccak.OfAnEmptyString : ValueKeccak.Compute(code.Span);
        worldState.InsertCode(address, codeHash, code, spec.Eip158, isGenesis);
    }

    public static bool InsertCode(this IWorldState worldState, Address address, in ValueHash256 codeHash,
        ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => worldState.InsertCode(address, in codeHash, code, spec.Eip158, isGenesis);

    public static void AddToBalance(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.AddToBalance(address, in balanceChange, spec.Eip158);

    public static bool AddToBalanceAndCreateIfNotExists(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec.Eip158);

    public static void SubtractFromBalance(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.SubtractFromBalance(address, in balanceChange, spec.Eip158);

    public static void Commit(this IWorldState worldState, IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        worldState.Commit(releaseSpec, NullStateTracer.Instance, isGenesis, commitRoots);
    }
}
