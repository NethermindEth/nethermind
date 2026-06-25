// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Stateless world state used inside the zkVM guest.
///
/// The world state (StateProvider / PersistentStorageProvider / trie / pools) is PERSISTENT and
/// reused across transactions.
/// </summary>
public class StatelessExecutingWorldState(IWorldState state) : WorldStateDecorator(state)
{
    /// <remarks>
    /// Resolving the code forces a lookup against the witness-backed code database,
    /// which fails if the bytecode was not included in the witness.
    /// </remarks>
    public override void RecordBytecodeAccess(Address address)
    {
        if (IsContract(address) && GetCode(address) is null)
            throw new InvalidOperationException($"Missing bytecode at address {address}");
    }

    // ---- Reads that populate the persistent caches (_blockChanges, storage caches, trie) ----

    public override bool TryGetAccount(Address address, out AccountStruct account)
        => base.TryGetAccount(address, out account);

    public override ulong GetNonce(Address address)
        => base.GetNonce(address);

    public override bool IsStorageEmpty(Address address)
        => base.IsStorageEmpty(address);

    public override ref readonly UInt256 GetBalance(Address address)
        => ref base.GetBalance(address);

    public override ref readonly ValueHash256 GetCodeHash(Address address)
        => ref base.GetCodeHash(address);

    public override byte[]? GetCode(Address address)
        => base.GetCode(address);

    public override byte[]? GetCode(in ValueHash256 codeHash)
        => base.GetCode(in codeHash);

    public override bool IsContract(Address address)
        => base.IsContract(address);

    public override bool AccountExists(Address address)
        => base.AccountExists(address);

    public override bool IsDeadAccount(Address address)
        => base.IsDeadAccount(address);

    public override ReadOnlySpan<byte> GetOriginal(in StorageCell storageCell)
        => base.GetOriginal(in storageCell);

    public override ReadOnlySpan<byte> Get(in StorageCell storageCell)
        => base.Get(in storageCell);

    public override ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
        => base.GetTransientState(in storageCell);

    // ---- Mutations of the persistent world state ----

    public override void Set(in StorageCell storageCell, byte[] newValue)
        => base.Set(in storageCell, newValue);

    public override void SetTransientState(in StorageCell storageCell, byte[] newValue)
        => base.SetTransientState(in storageCell, newValue);

    public override void Reset(bool resetBlockChanges = true)
        => base.Reset(resetBlockChanges);

    public override Snapshot TakeSnapshot(bool newTransactionStart = false)
        => base.TakeSnapshot(newTransactionStart);

    public override void Restore(Snapshot snapshot)
        => base.Restore(snapshot);

    public override void WarmUp(AccessList? accessList)
        => base.WarmUp(accessList);

    public override void WarmUp(Address address)
        => base.WarmUp(address);

    public override void ClearStorage(Address address)
        => base.ClearStorage(address);

    public override void RecalculateStateRoot()
        => base.RecalculateStateRoot();

    public override void DeleteAccount(Address address)
        => base.DeleteAccount(address);

    public override void CreateAccount(Address address, in UInt256 balance, in ulong nonce = default)
        => base.CreateAccount(address, in balance, in nonce);

    public override void CreateAccountIfNotExists(Address address, in UInt256 balance, in ulong nonce = default)
        => base.CreateAccountIfNotExists(address, in balance, in nonce);

    public override void CreateEmptyAccountIfDeleted(Address address)
        => base.CreateEmptyAccountIfDeleted(address);

    public override bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        => base.InsertCode(address, in codeHash, code, spec, isGenesis);

    public override void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => base.AddToBalance(address, in balanceChange, spec, out oldBalance);

    public override bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => base.AddToBalanceAndCreateIfNotExists(address, in balanceChange, spec, out oldBalance);

    public override void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec, out UInt256 oldBalance)
        => base.SubtractFromBalance(address, in balanceChange, spec, out oldBalance);

    public override void IncrementNonce(Address address, ulong delta, out ulong oldNonce)
        => base.IncrementNonce(address, delta, out oldNonce);

    public override void DecrementNonce(Address address, ulong delta)
        => base.DecrementNonce(address, delta);

    public override void SetNonce(Address address, in ulong nonce)
        => base.SetNonce(address, in nonce);

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
        => base.Commit(releaseSpec, tracer, isGenesis, commitRoots);

    public override void CommitTree(ulong blockNumber)
        => base.CommitTree(blockNumber);

    public override ArrayPoolList<AddressAsKey>? GetAccountChanges()
        => base.GetAccountChanges();

    public override void ResetTransient()
        => base.ResetTransient();

    public override void AddAccountRead(Address address)
        => base.AddAccountRead(address);
}
