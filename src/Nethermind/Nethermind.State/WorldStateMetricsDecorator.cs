// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.State;

public class WorldStateMetricsDecorator(IWorldState innerWorldState) : WrappedWorldState(innerWorldState)
{
    public override void Reset(bool resetBlockChanges = true)
    {
        StateMerkleizationTime = 0d;
        _innerWorldState.Reset(resetBlockChanges);
    }

    public override void RecalculateStateRoot()
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.RecalculateStateRoot();
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public double StateMerkleizationTime { get; private set; }

    public override void DeleteAccount(Address address)
        => _innerWorldState.DeleteAccount(address);

    public override void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
        => _innerWorldState.CreateAccount(address, in balance, in nonce);

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
        => _innerWorldState.CreateAccountIfNotExists(address, in balance, in nonce);

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => _innerWorldState.InsertCode(address, in codeHash, code, spec, isGenesis);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.AddToBalance(address, in balanceChange, spec);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec);

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        => _innerWorldState.SubtractFromBalance(address, in balanceChange, spec);

    public override void IncrementNonce(Address address, UInt256 delta)
        => _innerWorldState.IncrementNonce(address, delta);

    public override void DecrementNonce(Address address, UInt256 delta)
        => _innerWorldState.DecrementNonce(address, delta);

    public override void SetNonce(Address address, in UInt256 nonce)
        => _innerWorldState.SetNonce(address, nonce);

    public override void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public override void CommitTree(long blockNumber)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.CommitTree(blockNumber);
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
