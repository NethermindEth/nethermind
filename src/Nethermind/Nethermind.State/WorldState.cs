// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State
{
    public class WorldState : IWorldState
    {
        public WorldState(IStateProvider stateProvider, IStorageProvider storageProvider)
        {
            StateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            StorageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        }

        public IStateProvider StateProvider { get; }

        public void TouchCode(Keccak codeHash)
        {
            StateProvider.TouchCode(codeHash);
        }
        public void Commit(IStorageTracer stateTracer)
        {
            StorageProvider.Commit(stateTracer);
        }
        Snapshot.Storage IStorageProvider.TakeSnapshot(bool newTransactionStart)
        {
            return StorageProvider.TakeSnapshot(newTransactionStart);
        }
        public void ClearStorage(Address address)
        {
            StorageProvider.ClearStorage(address);
        }
        int IStateProvider.TakeSnapshot(bool newTransactionStart)
        {
            return StateProvider.TakeSnapshot(newTransactionStart);
        }
        public Snapshot TakeSnapshot(bool newTransactionStart = false)
        {
            Snapshot.Storage storageSnapshot = StorageProvider.TakeSnapshot(newTransactionStart);
            return new(StateProvider.TakeSnapshot(), storageSnapshot);
        }

        public void Restore(Snapshot snapshot)
        {
            StateProvider.Restore(snapshot.StateSnapshot);
            StorageProvider.Restore(snapshot.StorageSnapshot);
        }

        public IStorageProvider StorageProvider { get; }


        public Account GetAccount(Address address)
        {
            return StateProvider.GetAccount(address);
        }
        public void RecalculateStateRoot()
        {
            StateProvider.RecalculateStateRoot();
        }

        public Keccak StateRoot
        {
            get => StateProvider.StateRoot;
            set => StateProvider.StateRoot = value;
        }

        public void DeleteAccount(Address address)
        {
            StateProvider.DeleteAccount(address);
        }
        public void CreateAccount(Address address, in UInt256 balance)
        {
            StateProvider.CreateAccount(address, in balance);
        }
        public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
        {
            StateProvider.CreateAccount(address, in balance, in nonce);
        }
        public void UpdateCodeHash(Address address, Keccak codeHash, IReleaseSpec spec, bool isGenesis = false)
        {
            StateProvider.UpdateCodeHash(address, codeHash, spec, isGenesis);
        }
        public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            StateProvider.AddToBalance(address, in balanceChange, spec);
        }
        public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
        {
            StateProvider.SubtractFromBalance(address, in balanceChange, spec);
        }
        public void UpdateStorageRoot(Address address, Keccak storageRoot)
        {
            StateProvider.UpdateStorageRoot(address, storageRoot);
        }
        public void IncrementNonce(Address address)
        {
            StateProvider.IncrementNonce(address);
        }
        public void DecrementNonce(Address address)
        {
            StateProvider.DecrementNonce(address);
        }
        public Keccak UpdateCode(ReadOnlyMemory<byte> code)
        {
            return StateProvider.UpdateCode(code);
        }
        public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
        {
            StateProvider.Commit(releaseSpec, isGenesis);
            StorageProvider.Commit();
        }
        public void Commit(IReleaseSpec releaseSpec, IStateTracer? stateTracer, bool isGenesis = false)
        {
            StateProvider.Commit(releaseSpec, stateTracer, isGenesis);
        }

        public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool isGenesis = false)
        {
            StateProvider.Commit(releaseSpec, stateTracer, isGenesis);
            StorageProvider.Commit(stateTracer);
        }

        public byte[] GetOriginal(in StorageCell storageCell)
        {
            return StorageProvider.GetOriginal(storageCell);
        }
        public byte[] Get(in StorageCell storageCell)
        {
            return StorageProvider.Get(storageCell);
        }
        public void Set(in StorageCell storageCell, byte[] newValue)
        {
            StorageProvider.Set(storageCell, newValue);
        }
        public byte[] GetTransientState(in StorageCell storageCell)
        {
            return StorageProvider.GetTransientState(storageCell);
        }
        public void SetTransientState(in StorageCell storageCell, byte[] newValue)
        {
            StorageProvider.SetTransientState(storageCell, newValue);
        }
        public void Reset()
        {
            StateProvider.Reset();
            StorageProvider.Reset();
        }
        public void CommitTrees(long blockNumber)
        {
            StorageProvider.CommitTrees(blockNumber);
        }
        public void Commit()
        {
            StorageProvider.Commit();
        }
        public void CommitTree(long blockNumber)
        {
            StateProvider.CommitTree(blockNumber);
        }
        public UInt256 GetNonce(Address address)
        {
            return StateProvider.GetNonce(address);
        }
        public UInt256 GetBalance(Address address)
        {
            return StateProvider.GetBalance(address);
        }
        public Keccak GetStorageRoot(Address address)
        {
            return StateProvider.GetStorageRoot(address);
        }
        public byte[] GetCode(Address address)
        {
            return StateProvider.GetCode(address);
        }
        public byte[] GetCode(Keccak codeHash)
        {
            return StateProvider.GetCode(codeHash);
        }
        public Keccak GetCodeHash(Address address)
        {
            return StateProvider.GetCodeHash(address);
        }
        public void Accept(ITreeVisitor visitor, Keccak stateRoot, VisitingOptions? visitingOptions = null)
        {
            StateProvider.Accept(visitor, stateRoot, visitingOptions);
        }
        public bool AccountExists(Address address)
        {
            return StateProvider.AccountExists(address);
        }
        public bool IsDeadAccount(Address address)
        {
            return StateProvider.IsDeadAccount(address);
        }
        public bool IsEmptyAccount(Address address)
        {
            return StateProvider.IsEmptyAccount(address);
        }
        public void Restore(int snapshot)
        {
            StateProvider.Restore(snapshot);
        }
        public void Restore(Snapshot.Storage snapshot)
        {
            StorageProvider.Restore(snapshot);
        }
    }
}
