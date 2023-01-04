// // SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only
//
// using System;
// using System.Collections.Generic;
// using Nethermind.Core;
// using Nethermind.Core.Collections;
// using Nethermind.Core.Crypto;
// using Nethermind.Core.Resettables;
// using Nethermind.Core.Specs;
// using Nethermind.Int256;
// using Nethermind.Logging;
// using Nethermind.Trie;
// using Nethermind.Trie.Pruning;
//
// namespace Nethermind.State.StateStore;
//
// public class WorldStateProvider: IWorldStateProvider
// {
//     private readonly ResettableDictionary<Address, Stack<int>> _intraBlockStateCache = new ResettableDictionary<Address, Stack<int>>();
//     private readonly ResettableDictionary<Address, StorageTree> _storages = new ResettableDictionary<Address, StorageTree>();
//     private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new ResettableDictionary<StorageCell, byte[]>();
//     private readonly ResettableHashSet<StorageCell> _committedStorageThisRound = new ResettableHashSet<StorageCell>();
//     private readonly ResettableHashSet<Address> _committedStateThisRound = new ResettableHashSet<Address>();
//     private readonly ResettableDictionary<StorageCell, StackList<int>> _intraBlockStorageCache = new ResettableDictionary<StorageCell, StackList<int>>();
//
//     private readonly Stack<int> _transactionChangesSnapshots = new Stack<int>();
//     private static readonly byte[] _zeroValue = new byte[]
//     {
//         0
//     };
//
//     private readonly List<Change> _keptInCache = new List<Change>();
//     private readonly ILogger _logger;
//     private readonly IKeyValueStore _codeDb;
//
//     private const int StartCapacity = Resettable.StartCapacity;
//     private int _capacity = StartCapacity;
//     private Change?[] _changes = new Change?[StartCapacity];
//     private int _currentPosition = Resettable.EmptyPosition;
//
//     private readonly IStateStore _stateStore;
//
//     public Keccak StateRoot
//     {
//         get
//         {
//             return new Keccak(_stateStore.StateRoot);
//         }
//
//         set
//         {
//             _stateStore.StateRoot = value.Bytes;
//         }
//     }
//
//     private WorldStateProvider(ITrieStore? trieStore, IKeyValueStore? codeDb, ILogManager? logManager)
//     {
//         _logger = logManager?.GetClassLogger<StateProvider>() ?? throw new ArgumentNullException(nameof(logManager));
//         _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
//         _stateStore = new StateStore(
//             trieStore ?? throw new ArgumentNullException(nameof(trieStore)),
//             codeDb ?? throw new ArgumentNullException(nameof(codeDb)),
//             logManager
//         );
//
//     }
//
//     public void Restore(Snapshot snapshot)
//     {
//         throw new NotImplementedException();
//     }
//     public Account GetAccount(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void RecalculateStateRoot()
//     {
//         throw new NotImplementedException();
//     }
//
//     public void DeleteAccount(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void CreateAccount(Address address, in UInt256 balance)
//     {
//         throw new NotImplementedException();
//     }
//     public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
//     {
//         throw new NotImplementedException();
//     }
//     public void InsertCode(Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
//     {
//         throw new NotImplementedException();
//     }
//     public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
//     {
//         throw new NotImplementedException();
//     }
//     public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec)
//     {
//         throw new NotImplementedException();
//     }
//     public void UpdateStorageRoot(Address address, Keccak storageRoot)
//     {
//         throw new NotImplementedException();
//     }
//     public void IncrementNonce(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void DecrementNonce(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
//     {
//         throw new NotImplementedException();
//     }
//     public void Commit(IReleaseSpec releaseSpec, IStateTracer? stateTracer, bool isGenesis = false)
//     {
//         throw new NotImplementedException();
//     }
//     void IWorldStateProvider.Reset()
//     {
//         throw new NotImplementedException();
//     }
//     Snapshot IWorldStateProvider.TakeSnapshot(bool newTransactionStart)
//     {
//         throw new NotImplementedException();
//     }
//     public byte[] GetOriginal(StorageCell storageCell)
//     {
//         throw new NotImplementedException();
//     }
//     public byte[] Get(StorageCell storageCell)
//     {
//         throw new NotImplementedException();
//     }
//     public void Set(StorageCell storageCell, byte[] newValue)
//     {
//         throw new NotImplementedException();
//     }
//     public byte[] GetTransientState(StorageCell storageCell)
//     {
//         throw new NotImplementedException();
//     }
//     public void SetTransientState(StorageCell storageCell, byte[] newValue)
//     {
//         throw new NotImplementedException();
//     }
//     void IStorageProvider.Reset()
//     {
//         throw new NotImplementedException();
//     }
//     public void CommitTrees(long blockNumber)
//     {
//         throw new NotImplementedException();
//     }
//     public void Commit()
//     {
//         throw new NotImplementedException();
//     }
//     public void Commit(IStorageTracer stateTracer)
//     {
//         throw new NotImplementedException();
//     }
//     Snapshot.Storage IStorageProvider.TakeSnapshot(bool newTransactionStart)
//     {
//         throw new NotImplementedException();
//     }
//     public void ClearStorage(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     void IStateProvider.Reset()
//     {
//         throw new NotImplementedException();
//     }
//     public void CommitTree(long blockNumber)
//     {
//         throw new NotImplementedException();
//     }
//     public void TouchCode(Keccak codeHash)
//     {
//         throw new NotImplementedException();
//     }
//     int IStateProvider.TakeSnapshot(bool newTransactionStart)
//     {
//         throw new NotImplementedException();
//     }
//     public UInt256 GetNonce(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public UInt256 GetBalance(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public Keccak GetStorageRoot(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public byte[] GetCode(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public byte[] GetCode(Keccak codeHash)
//     {
//         throw new NotImplementedException();
//     }
//     public Keccak GetCodeHash(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void Accept(ITreeVisitor visitor, Keccak stateRoot, VisitingOptions? visitingOptions = null)
//     {
//         throw new NotImplementedException();
//     }
//     public bool AccountExists(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public bool IsDeadAccount(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public bool IsEmptyAccount(Address address)
//     {
//         throw new NotImplementedException();
//     }
//     public void Restore(int snapshot)
//     {
//         throw new NotImplementedException();
//     }
//     public void Restore(Snapshot.Storage snapshot)
//     {
//         throw new NotImplementedException();
//     }
//
//     private enum ChangeType
//     {
//         JustCache,
//         Touch,
//         Update,
//         New,
//         Delete,
//         Destroy
//     }
//
//     private class Change
//     {
//         public Change(ChangeType type, Address address, Account? account)
//         {
//             ChangeType = type;
//             Address = address;
//             Account = account;
//         }
//
//         public Change(ChangeType changeType, StorageCell storageCell, byte[] value)
//         {
//             StorageCell = storageCell;
//             Value = value;
//             ChangeType = changeType;
//         }
//
//         public ChangeType ChangeType { get; }
//         public Address Address { get; }
//         public StorageCell StorageCell { get; }
//         public byte[] Value { get; }
//         public Account? Account { get; }
//     }
// }
