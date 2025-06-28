
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing.GethStyle;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Generic;
using Nethermind.Blockchain;
using static Nethermind.Evm.VirtualMachine;
using System.Reflection;
using Nethermind.Core.Test.Builders;
using System.Linq;
using Microsoft.Extensions.Options;
using Nethermind.Specs.Forks;
using Nethermind.Abi;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Evm.Test.ILEVM;
using Nethermind.Crypto;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using Google.Protobuf.WellKnownTypes;
using static Microsoft.FSharp.Core.ByRefKinds;
using Nethermind.Consensus.Processing;
using Grpc.Core;
using NUnit.Framework;
using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Nethermind.Core.Eip2930;
using Nethermind.State.Tracing;
using Nethermind.Core.Collections;
using Nethermind.Trie;

namespace Nethermind.Evm.Test.ILEVM;

public class MockWorldState : IWorldState
{
    byte[] _bytes = new byte[32];
    byte[] _bytesOriginal = new byte[32];

    WorldState _inner;

    Hash256 IWorldState.StateRoot { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    Hash256 IReadOnlyStateProvider.StateRoot => throw new NotImplementedException();


    public MockWorldState(UInt256 getValue, UInt256 getOriginalValue, WorldState inner)
    {
        _inner = inner;
        GetValue.ToBigEndian(_bytes);
        _bytes = _bytes.WithoutLeadingZeros().ToArray();
        GetOriginalValue.ToBigEndian(_bytesOriginal);
        _bytesOriginal = _bytesOriginal.WithoutLeadingZeros().ToArray();
    }

    public byte[] GetOriginal(in StorageCell storageCell)
    {

        var index = storageCell.Index;
        return _bytesOriginal;

    }
    public ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        var index = storageCell.Index;
        return _bytes.AsSpan();

    }
    public void Set(in StorageCell storageCell, byte[] newValue)
    {

        var index = storageCell.Index;
    }

    public UInt256 GetNonce(Address address) => _inner.GetNonce(address);
    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        return _inner.GetTransientState(storageCell);
    }

    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        _inner.SetTransientState(storageCell, newValue);
    }

    void IWorldState.Reset(bool resetBlockChanges)
    {
        _inner.Reset(resetBlockChanges);
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        return _inner.TakeSnapshot(newTransactionStart);
    }

    void IWorldState.WarmUp(AccessList? accessList)
    {
        _inner.WarmUp(accessList);
    }

    void IWorldState.WarmUp(Address address)
    {
        _inner.WarmUp(address);
    }

    void IWorldState.ClearStorage(Address address)
    {
        _inner.ClearStorage(address);
    }

    void IWorldState.RecalculateStateRoot()
    {
        _inner.RecalculateStateRoot();
    }

    void IWorldState.DeleteAccount(Address address)
    {
        _inner.DeleteAccount(address);
    }

    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        _inner.CreateAccount(address, balance, nonce);
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce)
    {
        _inner.CreateAccountIfNotExists(address, balance, nonce);
    }

    void IWorldState.InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis)
    {
        _inner.InsertCode(address, codeHash, code, spec, isGenesis);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _inner.AddToBalance(address, balanceChange, spec);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        return _inner.AddToBalanceAndCreateIfNotExists(address, balanceChange, spec);
    }

    void IWorldState.SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
    {
        _inner.SubtractFromBalance(address, balanceChange, spec);
    }

    void IWorldState.UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        _inner.UpdateStorageRoot(address, storageRoot);
    }

    void IWorldState.IncrementNonce(Address address, UInt256 delta)
    {
        _inner.IncrementNonce(address, delta);
    }

    void IWorldState.DecrementNonce(Address address, UInt256 delta)
    {
        _inner.DecrementNonce(address, delta);
    }

    void IWorldState.SetNonce(Address address, in UInt256 nonce)
    {
        _inner.SetNonce(address, nonce);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        _inner.Commit(releaseSpec, isGenesis, commitRoots);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        _inner.Commit(releaseSpec, tracer, isGenesis, commitRoots);
    }

    void IWorldState.CommitTree(long blockNumber)
    {
        _inner.CommitTree(blockNumber);
    }

    ArrayPoolList<AddressAsKey>? IWorldState.GetAccountChanges()
    {
        return null;
    }

    void IWorldState.ResetTransient()
    {
        _inner.ResetTransient();
    }

    public void Restore(Snapshot snapshot)
    {
        _inner.Restore(snapshot);
    }


    internal void Restore(int state, int persistentStorage, int transientStorage)
    {
        _inner.Restore(state, persistentStorage, transientStorage);
    }

    byte[]? IReadOnlyStateProvider.GetCode(Address address)
    {
        return _inner.GetCode(address);
    }

    byte[]? IReadOnlyStateProvider.GetCode(Hash256 codeHash)
    {
        return _inner.GetCode(codeHash);
    }

    byte[]? IReadOnlyStateProvider.GetCode(ValueHash256 codeHash)
    {
        return _inner.GetCode(codeHash);
    }

    bool IReadOnlyStateProvider.IsContract(Address address)
    {
        return _inner.IsContract(address);
    }

    void IReadOnlyStateProvider.Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions)
    {
        _inner.Accept(visitor, stateRoot, visitingOptions);
    }

    public bool AccountExists(Address address)
    {
        return _inner.AccountExists(address);
    }

    bool IReadOnlyStateProvider.IsDeadAccount(Address address)
    {
        return _inner.IsDeadAccount(address);
    }

    bool IReadOnlyStateProvider.IsEmptyAccount(Address address)
    {
        return _inner.IsEmptyAccount(address);
    }

    bool IReadOnlyStateProvider.HasStateForRoot(Hash256 stateRoot)
    {
        return _inner.HasStateForRoot(stateRoot);
    }

    public Account GetAccount(Address address)
    {
        return _inner.GetAccount(address);
    }
    bool IAccountStateProvider.TryGetAccount(Address address, out AccountStruct account)
    {
        account = GetAccount(address).ToStruct();
        return !account.IsTotallyEmpty;
    }
}
