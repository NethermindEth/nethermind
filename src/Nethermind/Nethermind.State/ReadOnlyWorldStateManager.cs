using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.State;

public class ReadOnlyWorldStateManager : IWorldStateManager
{
    private readonly StateReader? _stateReader;
    private readonly VerkleStateReader? _verkleStateReader;
    private readonly OverlayStateReader? _overlayStateReader;

    protected ulong TransitionStartTimestamp { get; set; }
    protected ulong TransitionStartBlockNumber { get; set; }
    protected ulong TransitionEndTimestamp { get; set; }
    protected ulong TransitionEndBlockNumber { get; set; }

    public ReadOnlyWorldStateManager(
        IDbProvider dbProvider,
        IDbProvider verkleDbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        IReadOnlyVerkleTreeStore readOnlyVerkleTrieStore,
        ILogManager logManager
    )
    {
        IReadOnlyDbProvider? readOnlyDbProvider = dbProvider.AsReadOnly(false);
        var codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        _stateReader = new StateReader(readOnlyTrieStore, codeDb, logManager);

        IReadOnlyDbProvider? readOnlyVerkleDbProvider = verkleDbProvider.AsReadOnly(false);
        var verkleCodeDb = readOnlyVerkleDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        _verkleStateReader = new VerkleStateReader(readOnlyVerkleTrieStore, verkleCodeDb, logManager);

        _overlayStateReader = new OverlayStateReader(_stateReader, _verkleStateReader);
    }

    public ReadOnlyWorldStateManager(
        IDbProvider dbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        ILogManager logManager
    )
    {
        IReadOnlyDbProvider? readOnlyDbProvider = dbProvider.AsReadOnly(false);
        var codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        _stateReader = new StateReader(readOnlyTrieStore, codeDb, logManager);
    }

    public ReadOnlyWorldStateManager(
        IDbProvider verkleDbProvider,
        IReadOnlyVerkleTreeStore readOnlyVerkleTrieStore,
        ILogManager logManager
    )
    {
        IReadOnlyDbProvider? readOnlyVerkleDbProvider = verkleDbProvider.AsReadOnly(false);
        var verkleCodeDb = readOnlyVerkleDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        _verkleStateReader = new VerkleStateReader(readOnlyVerkleTrieStore, verkleCodeDb, logManager);
    }

    public virtual IWorldState GetGlobalWorldState(Block block)
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual IWorldState GetGlobalWorldState(UInt256 blockNumber)
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual IWorldState GetGlobalWorldState(BlockHeader header)
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual IWorldState GetGlobalWorldState(ulong timestamp)
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual WorldState GetWorldState()
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual VerkleWorldState GetVerkleWorldState()
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public virtual OverlayWorldState GetOverlayWorldState()
    {
        throw new InvalidOperationException("global world state not supported");
    }

    public IStateReader GetGlobalStateReader(Block block)
    {
        return GetGlobalStateReader(block.Timestamp);
    }

    public IStateReader GetGlobalStateReader(BlockHeader header)
    {
        return GetGlobalStateReader(header.Timestamp);
    }

    public IStateReader GetGlobalStateReader(UInt256 blockNumber)
    {
        if (blockNumber < TransitionStartBlockNumber) return _stateReader ?? throw new NullReferenceException();;
        if (blockNumber < TransitionEndBlockNumber) return _overlayStateReader ?? throw new NullReferenceException();
        return _verkleStateReader ?? throw new NullReferenceException();
    }

    public IStateReader GetGlobalStateReader(ulong timestamp)
    {
        if (timestamp < TransitionStartTimestamp) return _stateReader ?? throw new NullReferenceException();
        if (timestamp < TransitionEndTimestamp) return _overlayStateReader ?? throw new NullReferenceException();
        return _verkleStateReader ?? throw new NullReferenceException();
    }

    public IStateReader GetOverlayStateReader()
    {
        return _overlayStateReader ?? throw new NullReferenceException();
    }

    public IWorldState CreateResettableWorldState()
    {
        throw new NotImplementedException();
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }}