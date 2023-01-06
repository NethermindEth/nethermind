// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.State
{
    /// <summary>
    /// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
    /// Current format is an intermittent form on the way to a better state management.
    /// </summary>
    public interface IWorldState : IJournal<Snapshot>, IStateProvider
    {
        new void RecalculateStateRoot();

        new Keccak StateRoot { get; set; }

        new void DeleteAccount(Address address);

        new void CreateAccount(Address address, in UInt256 balance);

        new void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce);

        new void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false);

        new void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        new void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

        new void UpdateStorageRoot(Address address, Keccak storageRoot);

        new void IncrementNonce(Address address);

        new void DecrementNonce(Address address);
        void SetNonce(Address address, in UInt256 nonce);

        new void TouchCode(Keccak codeHash);
        void ClearStorage(Address address);

        byte[] GetOriginal(in StorageCell storageCell);

        byte[] Get(in StorageCell storageCell);

        void Set(in StorageCell storageCell, byte[] newValue);

        byte[] GetTransientState(in StorageCell storageCell);

        void SetTransientState(in StorageCell storageCell, byte[] newValue);

        new void Reset();
        new void Commit(IReleaseSpec releaseSpec, bool isGenesis = false);
        void Commit(IReleaseSpec releaseSpec, IWorldStateTracer? stateTracer, bool isGenesis = false);
        new void CommitTree(long blockNumber);
        new Snapshot TakeSnapshot(bool newTransactionStart = false);
        Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
    }
}
