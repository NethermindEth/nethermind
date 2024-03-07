// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.State.Witnesses;
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
    protected readonly ResettableDictionary<Address, Stack<int>> _intraBlockCache = new ResettableDictionary<Address, Stack<int>>();
    private readonly ResettableHashSet<Address> _committedThisRound = new ResettableHashSet<Address>();

    private readonly List<Change> _keptInCache = new List<Change>();
    private readonly ILogger _logger;
    protected readonly IKeyValueStore _codeDb;

    private int _capacity = StartCapacity;
    protected Change?[] _changes = new Change?[StartCapacity];
    private int _currentPosition = Resettable.EmptyPosition;

    protected readonly VerkleStateTree _tree;
    private readonly VerkleStorageProvider _storageProvider;

    // Only guarding against hot duplicates so filter doesn't need to be too big
    // // Note:
    // // False negatives are fine as they will just result in a overwrite set
    // // False positives would be problematic as the code _must_ be persisted
    private readonly LruKeyCache<Hash256> _codeInsertFilter = new(2048, "Code Insert Filter");

    public VerkleWorldState(VerkleStateTree verkleTree, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _tree = verkleTree;
        _storageProvider = new VerkleStorageProvider(verkleTree, logManager);
    }

    public void InsertExecutionWitness(ExecutionWitness? executionWitness, Banderwagon root)
    {
        _tree.Reset();
        if (!_tree.InsertIntoStatelessTree(executionWitness, root))
        {
            throw new InvalidDataException("stateless tree cannot be created: invalid proof");
        }
    }

    internal VerkleWorldState(VerklePersistentStorageProvider storageProvider, VerkleStateTree verkleTree, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _tree = verkleTree;
        _storageProvider = new VerkleStorageProvider(storageProvider, new VerkleTransientStorageProvider(logManager));
    }

    public VerkleWorldState(IVerkleTreeStore verkleStateStore, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _tree = new VerkleStateTree(verkleStateStore, logManager);
        _storageProvider = new VerkleStorageProvider(_tree, logManager);
    }

    // create a state provider using execution witness
    public VerkleWorldState(ExecutionWitness? executionWitness, Banderwagon root, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<WorldState>() ?? throw new ArgumentNullException(nameof(logManager));
        _tree = VerkleStateTree.CreateStatelessTreeFromExecutionWitness(executionWitness, root, logManager);
        _codeDb = new MemDb();
        _storageProvider = new VerkleStorageProvider(_tree, logManager);
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

    public void Accept(ITreeVisitor? visitor, Hash256? stateRoot, VisitingOptions? visitingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ArgumentNullException.ThrowIfNull(stateRoot);

        _tree.Accept(visitor, stateRoot, visitingOptions);
    }

    public void RecalculateStateRoot()
    {
        // probably cache and store state root in the tree, and use this to fetch and update rootHash from db
        // no good reason to fetch root everytime from db
    }

    public Hash256 StateRoot
    {
        get => _tree.StateRoot;
        set => _tree.StateRoot = value;
    }

    public ExecutionWitness GenerateExecutionWitness(byte[][] keys, out Banderwagon rootPoint)
    {
        return _tree.GenerateExecutionWitnessFromStore(keys, out rootPoint);
    }

    public bool AccountExists(Address address)
    {
        if (_intraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return _changes[value.Peek()]!.ChangeType != ChangeType.Delete;
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

    public bool HasStateForRoot(Hash256 stateRoot) => _tree.HasStateForStateRoot(stateRoot);

    AccountStruct IAccountStateProvider.GetAccount(Address address)
    {
        return GetAccount(address).ToStruct();
    }

    public Account GetAccount(Address address)
    {
        return GetThroughCache(address) ?? Account.TotallyEmpty;
    }

    public bool IsDeadAccount(Address address)
    {
        Account? account = GetThroughCache(address);
        return account?.IsEmpty ?? true;
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

    public void InsertCode(Address address, Hash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        // Don't reinsert if already inserted. This can be the case when the same
        // code is used by multiple deployments. Either from factory contracts (e.g. LPs)
        // or people copy and pasting popular contracts
        if (!_codeInsertFilter.Get(codeHash))
        {
            if (!_codeDb.PreferWriteByArray)
            {
                _codeDb.PutSpan(codeHash.Bytes, code.Span);
            }
            else if (MemoryMarshal.TryGetArray(code, out ArraySegment<byte> codeArray)
                     && codeArray.Offset == 0
                     && codeArray.Count == code.Length)
            {
                _codeDb[codeHash.Bytes] = codeArray.Array;
            }
            else
            {
                _codeDb[codeHash.Bytes] = code.ToArray();
            }

            _codeInsertFilter.Set(codeHash);
        }

        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when updating code hash");
        }

        if (account.CodeHash != codeHash)
        {
            if (_logger.IsDebug) _logger.Debug($"  Update {address} C {account.CodeHash} -> {codeHash}");
            Account changedAccount = account.WithChangedCodeHash(codeHash, _codeDb[codeHash.Bytes]);
            PushUpdate(address, changedAccount);
        }
        else if (releaseSpec.IsEip158Enabled && !isGenesis)
        {
            if (_logger.IsTrace) _logger.Trace($"  Touch {address} (code hash)");
            if (account.IsEmpty)
            {
                PushTouch(address, account, releaseSpec, account.Balance.IsZero);
            }
        }
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

    public void IncrementNonce(Address address)
    {
        Account? account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when incrementing nonce");
        }

        Account changedAccount = account.WithChangedNonce(account.Nonce + 1);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }

    public void DecrementNonce(Address address)
    {
        Account account = GetThroughCache(address);
        if (account is null)
        {
            throw new InvalidOperationException($"Account {address} is null when decrementing nonce.");
        }

        Account changedAccount = account.WithChangedNonce(account.Nonce - 1);
        if (_logger.IsTrace) _logger.Trace($"  Update {address} N {account.Nonce} -> {changedAccount.Nonce}");
        PushUpdate(address, changedAccount);
    }

    public void TouchCode(in ValueHash256 codeHash)
    {
        if (_codeDb is WitnessingStore witnessingStore)
        {
            witnessingStore.Touch(codeHash.Bytes);
        }
    }

    public ValueHash256 GetCodeHash(Address address)
    {
        Account account = GetThroughCache(address);
        return account?.CodeHash ?? Keccak.OfAnEmptyString;
    }

    public byte[] GetCode(Hash256 codeHash)
    {
        byte[]? code = codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];
        if (code is null)
        {
            throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        return code;
    }

    public byte[] GetCode(ValueHash256 codeHash)
    {
        byte[]? code = codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];
        if (code is null)
        {
            if (codeHash == Keccak.Zero) code = Array.Empty<byte>();
            else
                throw new InvalidOperationException($"Code {codeHash} is missing from the database.");
        }

        return code;
    }

    public virtual byte[] GetCodeChunk(Address codeOwner, UInt256 codeChunk)
    {
        // TODO: add functionality to add and get from cache instead - because there can be case
        //   where the code was updated while executing
        Hash256? treeKey = AccountHeader.GetTreeKeyForCodeChunk(codeOwner.Bytes, codeChunk);
        byte[]? chunk = _tree.Get(treeKey);
        if (chunk is null)
        {
            throw new InvalidOperationException($"Code Chunk {chunk} is missing from the database.");
        }
        return chunk;
    }

    public ReadOnlyMemory<byte> GetCodeFromCodeChunksForStatelessProcessing(Address owner)
    {
        int length = (int)GetAccount(owner).CodeSize;
        if (0 >= length)
            return Array.Empty<byte>();

        int endIndex = length - 1;

        int endChunkId = endIndex / 31;
        int endChunkLoc = (endIndex % 31) + 1;

        byte[] codeSlice = new byte[endIndex  + 1];
        Span<byte> codeSliceSpan = codeSlice;

        for (int i = 0; i < endChunkId; i++)
        {
            Hash256? treeKey = AccountHeader.GetTreeKeyForCodeChunk(owner.Bytes, (UInt256)i);
            byte[]? chunk = _tree.Get(treeKey);
            chunk?[1..].CopyTo(codeSliceSpan);
            codeSliceSpan = codeSliceSpan[31..];
        }
        Hash256? treeKeyEndChunk = AccountHeader.GetTreeKeyForCodeChunk(owner.Bytes, (UInt256)endChunkId);
        byte[]? endChunk = _tree.Get(treeKeyEndChunk);
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

    public void ClearStorage(Address address)
    {
        throw new StateException.StateDeleteNotSupported();
    }

    public void CreateAccount(Address address, in UInt256 balance)
    {
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance}");
        Account account = balance.IsZero ? Account.TotallyEmpty : new Account(balance);
        PushNew(address, account);
    }


    public void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce)
    {
        if (_logger.IsTrace) _logger.Trace($"Creating account: {address} with balance {balance} and nonce {nonce}");
        Account account = (balance.IsZero && nonce.IsZero) ? Account.TotallyEmpty : new Account(nonce, balance);
        PushNew(address, account);
    }

    public void AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balance, IReleaseSpec spec)
    {
        if (AccountExists(address))
        {
            AddToBalance(address, balance, spec);
        }
        else
        {
            CreateAccount(address, balance);
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
                        : _codeDb[beforeCodeHash.Bytes];
                byte[]? afterCode = afterCodeHash is null
                    ? null
                    : afterCodeHash == Keccak.OfAnEmptyString
                        ? Array.Empty<byte>()
                        : _codeDb[afterCodeHash.Bytes];

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
        Db.Metrics.StateTreeReads++;
        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);
        headerTreeKey[31] = AccountHeader.Version;
        IEnumerable<byte>? versionVal = _tree.Get(headerTreeKey);
        // here we have a assumption that the version value will always be set if the account is not null
        // so we can just use the version information to decide if the account exists or not
        if (versionVal is null) return null;
        UInt256 version = new((versionVal).ToArray());
        headerTreeKey[31] = AccountHeader.Balance;
        UInt256 balance = new((_tree.Get(headerTreeKey) ?? Array.Empty<byte>()).ToArray());
        headerTreeKey[31] = AccountHeader.Nonce;
        UInt256 nonce = new((_tree.Get(headerTreeKey) ?? Array.Empty<byte>()).ToArray());
        headerTreeKey[31] = AccountHeader.CodeHash;
        byte[]? codeHash = (_tree.Get(headerTreeKey) ?? Keccak.OfAnEmptyString.Bytes).ToArray();
        headerTreeKey[31] = AccountHeader.CodeSize;
        UInt256 codeSize = new((_tree.Get(headerTreeKey) ?? Array.Empty<byte>()).ToArray());

        return new Account(nonce, balance, codeSize, version, Keccak.EmptyTreeHash, new Hash256(codeHash));
    }

    protected void SetState(Address address, Account? account)
    {
        Db.Metrics.StateTreeWrites++;

        byte[] headerTreeKey = AccountHeader.GetTreeKeyPrefix(address.Bytes, 0);

        // TODO: is there a case where account is even null - anyways deleting a account is not supported in verkle trees
        if (account != null) _tree.InsertStemBatch(headerTreeKey.AsSpan()[..31], account.ToVerkleDict());
        else throw new StateException.StateDeleteNotSupported();
        if (account!.Code is not null) _tree.SetCode(address, account.Code);
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
        if (_intraBlockCache.TryGetValue(address, out Stack<int>? value))
        {
            return _changes[value!.Peek()]!.Account;
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
            && _changes[stack.Peek()]!.ChangeType == ChangeType.Touch)
        {
            return;
        }

        if (changeType == ChangeType.Delete)
        {
            if (!(stack.Count > 0 && _changes[stack.Peek()]?.ChangeType == ChangeType.New))
            {
                throw new StateException.StateDeleteNotSupported(
                    "Account can only be deleted when it was created in the same transaction");
            }
        }
        else
        {
            // this is to make sure that ChangeType for new account stays ChangeType.New so that we can handle Eip158 properly
            // for new account - we dont save Empty accounts, but for old accounts we can save empty accounts
            if (stack.Count > 0 && _changes[stack.Peek()]?.ChangeType == ChangeType.New)
            {
                changeType = ChangeType.New;
            }
        }

        IncrementChangePosition();
        stack.Push(_currentPosition);
        _changes[_currentPosition] = new Change(changeType, address, touchedAccount);
    }

    private void PushNew(Address address, Account account)
    {
        Stack<int> stack = SetupCache(address);
        IncrementChangePosition();
        stack.Push(_currentPosition);
        _changes[_currentPosition] = new Change(ChangeType.New, address, account);
    }

    private void IncrementChangePosition()
    {
        Resettable<Change>.IncrementPosition(ref _changes, ref _capacity, ref _currentPosition);
    }

    private Stack<int> SetupCache(Address address)
    {
        ref Stack<int>? value = ref _intraBlockCache.GetValueRefOrAddDefault(address, out bool exists);
        if (!exists)
        {
            value = new Stack<int>();
        }

        return value;
    }

    public virtual byte[] GetOriginal(in StorageCell storageCell)
    {
        return _storageProvider.GetOriginal(storageCell);
    }
    public virtual ReadOnlySpan<byte> Get(in StorageCell storageCell)
    {
        return _storageProvider.Get(storageCell).WithoutLeadingZeros().ToArray();
    }
    public void Set(in StorageCell storageCell, byte[] newValue)
    {
        _storageProvider.Set(storageCell, newValue.PadLeft(32));
    }
    public ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell)
    {
        return _storageProvider.GetTransientState(storageCell).WithoutLeadingZeros().ToArray();
    }
    public void SetTransientState(in StorageCell storageCell, byte[] newValue)
    {
        _storageProvider.SetTransientState(storageCell, newValue.PadLeft(32));
    }
    public void Reset()
    {
        if (_logger.IsTrace) _logger.Trace("Clearing state provider caches");
        _tree.Reset();
        _intraBlockCache.Reset();
        _committedThisRound.Reset();
        _nullAccountReads.Clear();
        _currentPosition = Resettable.EmptyPosition;
        Array.Clear(_changes, 0, _changes.Length);

        _storageProvider.Reset();
    }

    public void CommitTree(long blockNumber)
    {
        _tree.CommitTree(blockNumber);
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
        return new Snapshot(_currentPosition, _storageProvider.TakeSnapshot(newTransactionStart));
    }

    public void Commit(IReleaseSpec releaseSpec, bool isGenesis = false)
    {
        _storageProvider.Commit();
        Commit(releaseSpec, (IStateTracer)NullStateTracer.Instance, isGenesis);
    }

    public void Commit(IReleaseSpec releaseSpec, IWorldStateTracer stateTracer, bool isGenesis = false)
    {
        _storageProvider.Commit(stateTracer);
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
        if (_changes[_currentPosition] is null)
        {
            throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(WorldState)}");
        }

        if (_changes[_currentPosition + 1] is not null)
        {
            throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(WorldState)}");
        }

        bool isTracing = stateTracer.IsTracingState;
        Dictionary<Address, ChangeTrace> trace = null;
        if (isTracing)
        {
            trace = new Dictionary<Address, ChangeTrace>();
        }

        for (int i = 0; i <= _currentPosition; i++)
        {
            Change change = _changes[_currentPosition - i];
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
            Stack<int> stack = _intraBlockCache[change.Address];
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
                        SetState(change.Address, change.Account);
                        if (isTracing)
                        {
                            trace[change.Address] = new ChangeTrace(change.Account);
                        }

                        break;
                    }
                case ChangeType.New:
                    {
                        // For new accounts we do not need to save empty accounts when Eip158 enabled with Verkle
                        if (change.Account != null && (!releaseSpec.IsEip158Enabled || !change.Account.IsEmpty || isGenesis || change.Address == new Address("0xfffffffffffffffffffffffffffffffffffffffe")))
                        {
                            if (_logger.IsTrace) _logger.Trace($"  Commit create {change.Address} B = {change.Account.Balance} N = {change.Account.Nonce}");
                            SetState(change.Address, change.Account);
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
                            wasItCreatedNow |= _changes[previousOne]!.ChangeType == ChangeType.New;
                            if (wasItCreatedNow)
                            {
                                break;
                            }
                        }

                        if (!wasItCreatedNow)
                        {
                            SetState(change.Address, null);
                            if (isTracing)
                            {
                                trace[change.Address] = new ChangeTrace(null);
                            }
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

        _tree.Commit();
        Resettable<Change>.Reset(ref _changes, ref _capacity, ref _currentPosition);
        _committedThisRound.Reset();
        _nullAccountReads.Clear();
        _intraBlockCache.Reset();

        if (isTracing)
        {
            ReportChanges(stateTracer, trace);
        }
    }

    public void Restore(Snapshot snapshot)
    {
        Restore(snapshot.StateSnapshot);
        _storageProvider.Restore(snapshot.StorageSnapshot);
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
            Change change = _changes[_currentPosition - i];
            Stack<int> stack = _intraBlockCache[change!.Address];
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
                    _changes[actualPosition] = null;
                    continue;
                }
            }

            _changes[_currentPosition - i] = null; // TODO: temp, ???
            int forChecking = stack.Pop();
            if (forChecking != _currentPosition - i)
            {
                throw new InvalidOperationException($"Expected checked value {forChecking} to be equal to {_currentPosition} - {i}");
            }

            if (stack.Count == 0)
            {
                _intraBlockCache.Remove(change.Address);
            }
        }

        _currentPosition = snapshot;
        foreach (Change kept in _keptInCache)
        {
            _currentPosition++;
            _changes[_currentPosition] = kept;
            _intraBlockCache[kept.Address].Push(_currentPosition);
        }

        _keptInCache.Clear();
    }
}
