// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Trie;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.State;

public class VerkleWorldState : IWorldState
{
    public StateType StateType => StateType.Verkle;

    private const int StartCapacity = Resettable.StartCapacity;
    protected readonly ResettableDictionary<Address, Stack<int>> IntraBlockCache = new();
    private readonly HashSet<Address> _committedThisRound = [];

    private readonly List<Change> _keptInCache = [];
    private readonly ILogger _logger;
    private readonly IKeyValueStoreWithBatching _codeDb;
    private Dictionary<Hash256AsKey, byte[]> _codeBatch;
    private Dictionary<Hash256AsKey, byte[]>.AlternateLookup<ValueHash256> _codeBatchAlternate;


    private int _capacity = StartCapacity;
    protected Change?[] Changes = new Change?[StartCapacity];
    private int _currentPosition = Resettable.EmptyPosition;

    public readonly VerkleStateTree Tree;
    private readonly VerklePersistentStorageProvider _persistentStorageProvider;
    private readonly VerkleTransientStorageProvider _transientStorageProvider;

    // Only guarding against hot duplicates so filter doesn't need to be too big
    // Note:
    // False negatives are fine as they will just result in a overwrite set
    // False positives would be problematic as the code _must_ be persisted
    private readonly ClockKeyCacheNonConcurrent<ValueHash256> _codeInsertFilter = new(1_024);
    private readonly Dictionary<AddressAsKey, ChangeTrace> _blockChanges = new(4_096);



    public VerkleWorldState(VerkleStateTree tree, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches = null,
        bool populatePreBlockCache = true)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        Tree = tree;
        _persistentStorageProvider = new VerklePersistentStorageProvider(tree, preBlockCaches?.StorageCache, populatePreBlockCache, logManager);
        _transientStorageProvider = new VerkleTransientStorageProvider(logManager);
    }

    public void InsertExecutionWitness(ExecutionWitness? executionWitness, Banderwagon root)
    {
        Tree.Reset();
        if (!Tree.InsertIntoStatelessTree(executionWitness, root))
        {
            throw new InvalidDataException("stateless tree cannot be created: invalid proof");
        }
    }

    internal VerkleWorldState(VerklePersistentStorageProvider storageProvider, VerkleStateTree verkleTree, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches = null,
        bool populatePreBlockCache = true)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        Tree = verkleTree;
        _persistentStorageProvider = storageProvider;
        _transientStorageProvider = new VerkleTransientStorageProvider(logManager);
    }

    public VerkleWorldState(IVerkleTreeStore verkleStateStore, IKeyValueStoreWithBatching? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches = null,
        bool populatePreBlockCache = true)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        Tree = new VerkleStateTree(verkleStateStore, logManager);
        _persistentStorageProvider = new VerklePersistentStorageProvider(Tree, preBlockCaches?.StorageCache, populatePreBlockCache, logManager);
        _transientStorageProvider = new VerkleTransientStorageProvider(logManager);
    }

    // create a state provider using execution witness
    public VerkleWorldState(ExecutionWitness? executionWitness, Banderwagon root, ILogManager? logManager, PreBlockCaches? preBlockCaches = null,
        bool populatePreBlockCache = true)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        Tree = VerkleStateTree.CreateStatelessTreeFromExecutionWitness(executionWitness, root, logManager);
        _codeDb = new MemDb();
        _persistentStorageProvider = new VerklePersistentStorageProvider(Tree, preBlockCaches?.StorageCache, populatePreBlockCache, logManager);
        _transientStorageProvider = new VerkleTransientStorageProvider(logManager);
    }

    public bool ValuePresentInTree(Hash256 key)
    {
        return Tree.HasLeaf(key);
    }

    public bool IsContract(Address address)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            return false;
        }

        return account.IsContract;
    }

    // TODO: need to figure out a way to either merge the verkle tree visitor or need to find out a way to implement ITreeVisitor for verkle
    public void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public void Accept<TContext>(IVerkleTreeVisitor<TContext> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TContext : struct, INodeContext<TContext>
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ArgumentNullException.ThrowIfNull(stateRoot);

        Tree.Accept(visitor, stateRoot, visitingOptions);
    }

    public void RecalculateStateRoot()
    {
        // probably cache and store state root in the tree, and use this to fetch and update rootHash from db
        // no good reason to fetch root everytime from db
    }

    public Hash256 StateRoot
    {
        get => Tree.StateRoot;
        set => Tree.StateRoot = value;
    }

    public ExecutionWitness GenerateExecutionWitness(Hash256[] keys, out Banderwagon rootPoint)
    {
        return Tree.GenerateExecutionWitnessFromStore(keys, out rootPoint);
    }

    public void UpdateWithPostStateValues(ExecutionWitness executionWitness)
    {
        Tree.UpdateWithPostStateValues(executionWitness);
    }

    public bool AccountExists(Address address)
    {
        if (IntraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return Changes[value.Peek()]!.ChangeType != ChangeType.Delete;
        }

        return GetAndAddToCache(address) is not null;
    }

    public bool IsEmptyAccount(Address address)
    {
        Account account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when checking if empty");
        }

        return account.IsEmpty;
    }

    public bool HasStateForRoot(Hash256 stateRoot) => Tree.HasStateForStateRoot(stateRoot);

    public Account GetAccount(Address address)
    {
        return GetThroughCache(address) ?? Account.TotallyEmpty;
    }

    public bool IsDeadAccount(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.IsEmpty ?? true;
    }

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        account = GetAccount(address).ToStruct();
        return !account.IsTotallyEmpty;
    }

    public UInt256 GetNonce(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.Nonce ?? UInt256.Zero;
    }

    public ValueHash256 GetStorageRoot(Address address)
    {
        throw new InvalidOperationException($"no storage root in verkle trees");
    }

    public UInt256 GetBalance(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.Balance ?? UInt256.Zero;
    }

    public void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default)
    {
        if (AccountExists(address)) return;
        CreateAccount(address, balance, nonce);
    }

    public bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
    {
        bool inserted = false;
        // Don't reinsert if already inserted. This can be the case when the same
        // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
        // or people copy and pasting popular contracts
        if (!_codeInsertFilter.Get(codeHash))
        {
            if (_codeBatch is null)
            {
                _codeBatch = new(Hash256AsKeyComparer.Instance);
                _codeBatchAlternate = _codeBatch.GetAlternateLookup<ValueHash256>();
            }

            if (MemoryMarshal.TryGetArray(code, out ArraySegment<byte> codeArray)
                && codeArray.Offset == 0
                && codeArray.Count == code.Length)
            {
                _codeBatchAlternate[codeHash] = codeArray.Array;
            }
            else
            {
                _codeBatchAlternate[codeHash] = code.ToArray();
            }

            _codeInsertFilter.Set(codeHash);
            inserted = true;
        }

        Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when updating code hash");

        if (account.CodeHash.ValueHash256 != codeHash)
        {
            if (_logger.IsDebug) _logger.Debug($"  Update {address} C {account.CodeHash} -> {codeHash}");
            Account changedAccount = account.WithChangedCodeHash((UInt256)code.Length, (Hash256)codeHash);
            PushUpdate(address, changedAccount);
        }
        else if (spec.IsEip158Enabled && !isGenesis)
        {
            if (_logger.IsTrace) _logger.Trace($"  Touch {address} (code hash)");
            if (account.IsEmpty)
            {
                PushTouch(address, account, spec, account.Balance.IsZero);
            }
        }

        return inserted;
    }

    private void SetNewBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec, bool isSubtracting)
    {

        Account GetThroughCacheCheckExists()
        {
            Account result = GetThroughCache(address);
            if (result is null)
            {
                if (_logger.IsError) _logger.Error("Updating balance of a non-existing account");
                throw new InvalidOperationException("Updating balance of a non-existing account");
            }

            return result;
        }

        bool isZero = balanceChange.IsZero;
        if (isZero)
        {
            if (releaseSpec.IsEip158Enabled)
            {
                Account touched = GetThroughCacheCheckExists();
                if (_logger.IsTrace) _logger.Trace($"  Touch {address} (balance)");
                if (touched.IsEmpty)
                {
                    PushTouch(address, touched, releaseSpec, true);
                }
            }

            return;
        }

        Account account = GetThroughCacheCheckExists();

        if (isSubtracting && account.Balance < balanceChange)
        {
            throw new InsufficientBalanceException(address);
        }

        UInt256 newBalance = isSubtracting ? account.Balance - balanceChange : account.Balance + balanceChange;

        Account changedAccount = account.WithChangedBalance(newBalance);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} B {account.Balance} -> {newBalance} ({(isSubtracting ? "-" : "+")}{balanceChange})");
        PushUpdate(address, changedAccount);
    }

    public void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
    {
        SetNewBalance(address, balanceChange, releaseSpec, true);
    }

    public void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec releaseSpec)
    {
        SetNewBalance(address, balanceChange, releaseSpec, false);
    }

    /// <summary>
    /// This is a coupling point between storage provider and state provider.
    /// This is pointing at the architectural change likely required where Storage and State Provider are represented by a single world state class.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="storageRoot"></param>
    public void UpdateStorageRoot(Address address, Hash256 storageRoot)
    {
        throw new InvalidOperationException($"no storage root in verkle trees");
    }

    public void IncrementNonce(Address address, UInt256 delta)
    {
        Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        Account changedAccount = account.WithChangedNonce(account.Nonce + delta);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
        PushUpdate(address, changedAccount);
    }

    public void DecrementNonce(Address address, UInt256 delta)
    {
        Account? account = GetThroughCache(address) ?? throw new InvalidOperationException($"Account {address} is null when decrementing nonce.");
        Account changedAccount = account.WithChangedNonce(account.Nonce - delta);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce.ToHexString(skipLeadingZeros: true)} -> {changedAccount.Nonce.ToHexString(skipLeadingZeros: true)}");
        PushUpdate(address, changedAccount);
    }

    public ValueHash256 GetCodeHash(Address address)
    {
        Account account = GetThroughCache(address);
        return account?.CodeHash ?? Keccak.OfAnEmptyString;
    }

    public byte[] GetCode(Hash256 codeHash) => GetCodeCore(in codeHash.ValueHash256);

    public byte[]? GetCode(ValueHash256 codeHash) => GetCodeCore(in codeHash);

    // TODO: we need to see if this is the correct implemetation of this function - we need to cross check on where it
    // is actually used and add a comment here and see if we should check the code batch.
    public void ResetTransient()
    {
        _transientStorageProvider.Reset();
    }

    public virtual byte[] GetCodeChunk(Address codeOwner, UInt256 codeChunk)
    {
        // TODO: add functionality to add and get from cache instead - because there can be case
        //   where the code was updated while executing
        Hash256? treeKey = AccountHeader.GetTreeKeyForCodeChunk(codeOwner.Bytes, codeChunk);
        byte[]? chunk = Tree.Get(treeKey);
        if (chunk is null)
        {
            throw new InvalidOperationException($"Code Chunk {chunk} is missing from the database.");
        }
        return chunk;
    }

    private byte[] GetCodeCore(in ValueHash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString.ValueHash256) return [];

        if (_codeBatch is null || !_codeBatchAlternate.TryGetValue(codeHash, out byte[]? code))
        {
            code = _codeDb[codeHash.Bytes];
        }

        return code ?? throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
    }

    public ReadOnlyMemory<byte> GetCodeFromCodeChunksForStatelessProcessing(Address owner)
    {
        int length = (int)GetAccount(owner).CodeSize;
        if (0 >= length)
            return Array.Empty<byte>();

        int endIndex = length - 1;

        int endChunkId = endIndex / 31;
        int endChunkLoc = (endIndex % 31) + 1;

        byte[] codeSlice = new byte[endIndex + 1];
        Span<byte> codeSliceSpan = codeSlice;

        for (int i = 0; i < endChunkId; i++)
        {
            Hash256? treeKey = AccountHeader.GetTreeKeyForCodeChunk(owner.Bytes, (UInt256)i);
            byte[]? chunk = Tree.Get(treeKey);
            chunk?[1..].CopyTo(codeSliceSpan);
            codeSliceSpan = codeSliceSpan[31..];
        }
        Hash256? treeKeyEndChunk = AccountHeader.GetTreeKeyForCodeChunk(owner.Bytes, (UInt256)endChunkId);
        byte[]? endChunk = Tree.Get(treeKeyEndChunk);
        endChunk?[1..(endChunkLoc + 1)].CopyTo(codeSliceSpan);
        return codeSlice;
    }

    public byte[] GetCode(Address address)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            return Array.Empty<byte>();
        }

        return GetCode(account.CodeHash);
    }

    public void DeleteAccount(Address address)
    {
        PushDelete(address);
    }

    public void WarmUp(AccessList? accessList)
    {
        if (accessList?.IsEmpty == false)
        {
            foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
            {
                bool exists = GetState(address) is not null;
                foreach (UInt256 storage in storages)
                {
                    _persistentStorageProvider.WarmUp(new StorageCell(address, in storage), isEmpty: !exists);
                }
            }
        }
    }

    public void WarmUp(Address address)
    {
        GetState(address);
    }

    public void ClearStorage(Address address)
    {
        // throw new StateDeleteNotSupported();
    }

    public void CreateAccount(Address address, in UInt256 balance)
    {
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance}");
        Account account = balance.IsZero ? Account.TotallyEmpty : new Account(0, balance);
        PushNew(address, account);
    }


    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
    {
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance} and nonce {nonce}");
        Account account = (balance.IsZero && nonce.IsZero) ? Account.TotallyEmpty : new Account(0, nonce, balance);
        PushNew(address, account);
    }

    public bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec)
    {
        if (AccountExists(address))
        {
            AddToBalance(address, balance, spec);
            return false;
        }
        else
        {
            CreateAccount(address, balance);
            return true;
        }
    }

    private readonly struct ChangeTrace
    {
        public ChangeTrace(Account? before, Account? after)
        {
            After = after;
            Before = before;
        }

        public ChangeTrace(Account? after)
        {
            After = after;
            Before = null;
        }

        public Account? Before { get; }
        public Account? After { get; }
    }



    private void ReportChanges(IStateTracer stateTracer, Dictionary<Address, ChangeTrace> trace)
    {
        foreach ((Address address, ChangeTrace change) in trace)
        {
            bool someChangeReported = false;

            Account? before = change.Before;
            Account? after = change.After;

            UInt256? beforeBalance = before?.Balance;
            UInt256? afterBalance = after?.Balance;

            UInt256? beforeNonce = before?.Nonce;
            UInt256? afterNonce = after?.Nonce;

            Hash256? beforeCodeHash = before?.CodeHash;
            Hash256? afterCodeHash = after?.CodeHash;

            if (beforeCodeHash != afterCodeHash)
            {
                byte[]? beforeCode = beforeCodeHash is null
                    ? null
                    : beforeCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : GetCodeCore(beforeCodeHash.ValueHash256);
                byte[]? afterCode = afterCodeHash is null
                    ? null
                    : afterCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : GetCodeCore(afterCodeHash.ValueHash256);

                if (!((beforeCode?.Length ?? 0) == 0 && (afterCode?.Length ?? 0) == 0))
                {
                    stateTracer.ReportCodeChange(address, beforeCode, afterCode);
                }

                someChangeReported = true;
            }

            if (afterBalance != beforeBalance)
            {
                stateTracer.ReportBalanceChange(address, beforeBalance, afterBalance);
                someChangeReported = true;
            }

            if (afterNonce != beforeNonce)
            {
                stateTracer.ReportNonceChange(address, beforeNonce, afterNonce);
                someChangeReported = true;
            }

            if (!someChangeReported)
            {
                stateTracer.ReportAccountRead(address);
            }
        }
    }

    protected Account? GetState(Address address)
    {
        return Tree.Get(address);
    }

    protected void BulkSet(Dictionary<Address, (Account, bool)> accountChange)
    {
        void SetStateKeyValue(KeyValuePair<Address, (Account, bool)> keyValuePair)
        {
            SetState(keyValuePair.Key, keyValuePair.Value.Item1, keyValuePair.Value.Item2);
        }

        if (accountChange.Count == 1)
        {
            foreach (KeyValuePair<Address, (Account, bool)> keyValuePair in accountChange)
            {
                SetStateKeyValue(keyValuePair);
            }

            return;
        }

        var setStateAction = new ActionBlock<KeyValuePair<Address, (Account, bool)>>(
            SetStateKeyValue,
            new ExecutionDataflowBlockOptions()
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            });

        foreach (KeyValuePair<Address, (Account, bool)> keyValuePair in accountChange)
        {
            setStateAction.Post(keyValuePair);
        }

        setStateAction.Complete();
        setStateAction.Completion.Wait();
    }

    protected void SetState(Address address, Account account, bool isNew)
    {

        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);

        // TODO: is there a case where account is even null - anyways deleting a account is not supported in verkle trees

        LeafInSubTree[]? data = account.ToVerkleDict();
        if (isNew)
        {
            Tree.InsertStemBatch(headerTreeKey.AsSpan()[..31], data);
            // TODO: check if this thing always work - or there are edge cases that we would need to sort out
            if (account.CodeHash != Keccak.OfAnEmptyString)
            {
                Tree.SetCode(address, GetCodeCore(account.CodeHash.ValueHash256));
            }
        }
        else
        {
            if (account.CodeHash == Keccak.EmptyTreeHash) data = [data[0]];
            Tree.InsertStemBatch(headerTreeKey.AsSpan()[..31], data);
        }
    }

    protected readonly HashSet<Address> _nullAccountReads = [];

    protected virtual Account? GetAndAddToCache(Address address)
    {
        if (_nullAccountReads.Contains(address)) return null;

        Account? account = GetState(address);
        if (account is not null)
        {
            PushJustCache(address, account);
        }
        else
        {
            // just for tracing - potential perf hit, maybe a better solution?
            _nullAccountReads.Add(address);
        }

        return account;
    }

    private Account? GetThroughCache(Address address)
    {
        if (IntraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return Changes[value!.Peek()]!.Account;
        }

        Account account = GetAndAddToCache(address);
        return account;
    }

    protected void PushJustCache(Address address, Account account)
    {
        Push(ChangeType.JustCache, address, account);
    }

    private void PushUpdate(Address address, Account account)
    {
        Push(ChangeType.Update, address, account);
    }

    private void PushDelete(Address address)
    {
        Push(ChangeType.Delete, address, null);
    }

    private void PushTouch(Address address, Account account, IReleaseSpec releaseSpec, bool isZero)
    {
        if (isZero && releaseSpec.IsEip158IgnoredAccount(address)) return;
        Push(ChangeType.Touch, address, account);
    }

    private void Push(ChangeType changeType, Address address, Account? touchedAccount)
    {
        Stack<int> stack = SetupCache(address);
        if (changeType == ChangeType.Touch
            && Changes[stack.Peek()]!.ChangeType == ChangeType.Touch)
        {
            return;
        }

        if (changeType == ChangeType.Delete)
        {
            if (!(stack.Count > 0 && Changes[stack.Peek()]?.ChangeType == ChangeType.New))
            {
                throw new StateDeleteNotSupported(
                    $"Account can only be deleted when it was created in the same transaction {address}");
            }
            _persistentStorageProvider.ClearStorage(address);
        }
        else
        {
            // this is to make sure that ChangeType for new account stays ChangeType.New so that we can handle Eip158 properly
            // for new account - we dont save Empty accounts, but for old accounts we can save empty accounts
            if (stack.Count > 0 && Changes[stack.Peek()]?.ChangeType == ChangeType.New)
            {
                changeType = ChangeType.New;
            }
        }

        IncrementChangePosition();
        stack.Push(_currentPosition);
        Changes[_currentPosition] = new Change(changeType, address, touchedAccount);
    }

    private void PushNew(Address address, Account account)
    {
        Stack<int> stack = SetupCache(address);
        IncrementChangePosition();
        stack.Push(_currentPosition);
        Changes[_currentPosition] = new Change(ChangeType.New, address, account);
    }

    private void IncrementChangePosition()
    {
        Resettable<Change>.IncrementPosition(ref Changes, ref _capacity, ref _currentPosition);
    }

    private Stack<int> SetupCache(Address address)
    {
        ref Stack<int>? value = ref IntraBlockCache.GetValueRefOrAddDefault(address, out bool exists);
        if (!exists)
        {
            value = new Stack<int>();
        }

        return value;
    }

    public virtual byte[] GetOriginal(in StorageCell storageCell)
    {
        return _persistentStorageProvider.GetOriginal(storageCell);
    }
    public virtual ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        return _persistentStorageProvider.Get(storageCell).WithoutLeadingZeros().ToArray();
    }
    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        _persistentStorageProvider.Set(storageCell, newValue.PadLeft(32));
    }
    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        return _transientStorageProvider.Get(storageCell).WithoutLeadingZeros().ToArray();
    }
    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        _transientStorageProvider.Set(storageCell, newValue.PadLeft(32));
    }

    public void Reset(bool resetBlockChanges = true)
    {
        if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
        Tree.Reset();
        IntraBlockCache.Reset();
        _committedThisRound.Clear();
        _nullAccountReads.Clear();
        _currentPosition = Resettable.EmptyPosition;
        Array.Clear(Changes, 0, Changes.Length);

        _persistentStorageProvider.Reset(resetBlockChanges);
        _transientStorageProvider.Reset(resetBlockChanges);
    }

    public void CommitTree(long blockNumber)
    {
        Tree.CommitTree(blockNumber);
    }

    public ArrayPoolList<AddressAsKey>? GetAccountChanges()
    {
        int count = _blockChanges.Count;
        if (count == 0)
        {
            return null;
        }
        else
        {
            ArrayPoolList<AddressAsKey> addresses = new(count);
            foreach (AddressAsKey address in _blockChanges.Keys)
            {
                addresses.Add(address);
            }
            return addresses;
        }
    }


    // used in EthereumTests
    public void SetNonce(Address address, in UInt256 nonce)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        }

        Account changedAccount = account.WithChangedNonce(nonce);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }
    protected enum ChangeType
    {
        JustCache,
        Touch,
        Update,
        New,
        Delete
    }

    protected class Change
    {
        public Change(ChangeType type, Address address, Account? account)
        {
            ChangeType = type;
            Address = address;
            Account = account;
        }

        public ChangeType ChangeType { get; }
        public Address Address { get; }
        public Account? Account { get; }
    }

    public Snapshot TakeSnapshot(bool newTransactionStart = false)
    {
        int persistentSnapshot = _persistentStorageProvider.TakeSnapshot(newTransactionStart);
        int transientSnapshot = _transientStorageProvider.TakeSnapshot(newTransactionStart);
        Snapshot.Storage storageSnapshot = new Snapshot.Storage(persistentSnapshot, transientSnapshot);
        int stateSnapshot = _currentPosition;
        return new Snapshot(stateSnapshot, storageSnapshot);
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        _persistentStorageProvider.Commit();
        _transientStorageProvider.Commit();
        Commit(releaseSpec, (IStateTracer)NullStateTracer.Instance, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool isGenesis = false, bool commitRoots = true)
    {
        _persistentStorageProvider.Commit(stateTracer);
        _transientStorageProvider.Commit(stateTracer);
        Commit(releaseSpec, (IStateTracer)stateTracer, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IStateTracer stateTracer, bool isGenesis = false)
    {
        if (_currentPosition == -1)
        {
            if (_logger.IsTrace) _logger.Trace("  no state changes to commit");
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"Committing state changes (at {_currentPosition})");
        if (Changes[_currentPosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(WorldState)}");
        }

        if (Changes[_currentPosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(WorldState)}");
        }

        bool isTracing = stateTracer.IsTracingState;
        Dictionary<Address, ChangeTrace> trace = null;
        if (isTracing)
        {
            trace = new Dictionary<Address, ChangeTrace>();
        }

        var accountChange = new Dictionary<Address, (Account, bool)>();
        for (int i = 0; i <= _currentPosition; i++)
        {
            Change change = Changes[_currentPosition - i];
            if (!isTracing && change!.ChangeType == ChangeType.JustCache)
            {
                continue;
            }

            if (_committedThisRound.Contains(change!.Address))
            {
                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    trace[change.Address] = new ChangeTrace(change.Account, trace[change.Address].After);
                }

                continue;
            }

            // because it was not committed yet it means that the just cache is the only state (so it was read only)
            if (isTracing && change.ChangeType == ChangeType.JustCache)
            {
                _nullAccountReads.Add(change.Address);
                continue;
            }
            Stack<int> stack = IntraBlockCache[change.Address];
            int forAssertion = stack.Pop();
            if (forAssertion != _currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
            }

            _committedThisRound.Add(change.Address);

            switch (change.ChangeType)
            {
                case ChangeType.JustCache:
                    {
                        break;
                    }
                case ChangeType.Touch:
                case ChangeType.Update:
                    {
                        // No need for account deletion here even when Eip158 is enabled because we should not delete
                        // anything from the tree that is already there
                        if (_logger.IsTrace)
                            if (change.Account != null)
                                _logger.Trace($"  Commit update {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce} C = {change.Account.CodeHash}");
                        accountChange[change.Address] = (change.Account, false);
                        if (isTracing)
                        {
                            trace[change.Address] = new ChangeTrace(change.Account);
                        }

                        break;
                    }
                case ChangeType.New:
                    {
                        // For new accounts we do not need to save empty accounts when Eip158 enabled with Verkle
                        if (change.Account != null && (!releaseSpec.IsEip158Enabled || !change.Account.IsEmpty || isGenesis))
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit create {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            accountChange[change.Address] = (change.Account, true);
                            if (isTracing)
                            {
                                trace[change.Address] = new ChangeTrace(change.Account);
                            }
                        }

                        break;
                    }
                case ChangeType.Delete:
                    {
                        if (_logger.IsTrace) _logger.Trace($"  Commit remove {change.Address}");
                        bool wasItCreatedNow = false;
                        while (stack.Count > 0)
                        {
                            int previousOne = stack.Pop();
                            wasItCreatedNow |= Changes[previousOne]!.ChangeType == ChangeType.New;
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            throw new StateDeleteNotSupported();
                        }

                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        if (isTracing)
        {
            foreach (Address nullRead in _nullAccountReads)
            {
                // // this may be enough, let us write tests
                stateTracer.ReportAccountRead(nullRead);
            }
        }

        BulkSet(accountChange);
        Tree.Commit();
        Resettable<Change>.Reset(ref Changes, ref _capacity, ref _currentPosition);
        _committedThisRound.Clear();
        _nullAccountReads.Clear();
        IntraBlockCache.Reset();

        if (isTracing)
        {
            ReportChanges(stateTracer, trace);
        }
    }

    public void Restore(Snapshot snapshot)
    {
        Restore(snapshot.StateSnapshot);
        _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);
        _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);
    }

    public void Restore(int snapshot)
    {
        if (snapshot > _currentPosition)
        {
            throw new InvalidOperationException($"{nameof(WorldState)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
        }

        if (_logger.IsTrace) _logger.Trace($"Restoring state snapshot {snapshot}");
        if (snapshot == _currentPosition)
        {
            return;
        }

        for (int i = 0; i < _currentPosition - snapshot; i++)
        {
            Change change = Changes[_currentPosition - i];
            Stack<int> stack = IntraBlockCache[change!.Address];
            if (stack.Count == 1)
            {
                if (change.ChangeType == ChangeType.JustCache)
                {
                    int actualPosition = stack.Pop();
                    if (actualPosition != _currentPosition - i)
                    {
                        throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                    }

                    _keptInCache.Add(change);
                    Changes[actualPosition] = null;
                    continue;
                }
            }

            Changes[_currentPosition - i] = null; // TODO: temp, ???
            int forChecking = stack.Pop();
            if (forChecking != _currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {_currentPosition} - {i}");
            }

            if (stack.Count == 0)
            {
                IntraBlockCache.Remove(change.Address);
            }
        }

        _currentPosition = snapshot;
        foreach (Change kept in _keptInCache)
        {
            _currentPosition++;
            Changes[_currentPosition] = kept;
            IntraBlockCache[kept.Address].Push(_currentPosition);
        }

        _keptInCache.Clear();
    }
}
