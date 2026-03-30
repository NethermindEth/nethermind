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
        worldState.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public static void Commit(this IWorldState worldState, IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
        => worldState.Commit(releaseSpec, NullStateTracer.Instance, isGenesis, commitRoots);

    public static void AddToBalance(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.AddToBalance(address, balanceChange, spec, out _);

    public static bool AddToBalanceAndCreateIfNotExists(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec, out _);

    public static void SubtractFromBalance(this IWorldState worldState, Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => worldState.SubtractFromBalance(address, balanceChange, spec, out _);

    public static void IncrementNonce(this IWorldState worldState, Address address, UInt256 delta)
        => worldState.IncrementNonce(address, delta, out _);

    public static void IncrementNonce(this IWorldState worldState, Address address)
        => worldState.IncrementNonce(address, UInt256.One);

    public static void AddAccountRead(this IWorldState worldState, Address address)
        => (worldState as IBlockAccessListBuilder)?.AddAccountRead(address);
}
