using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.State;

public class WorldStateManager : ReadOnlyWorldStateManager
{
    private WorldState? _worldState;
    private VerkleWorldState? _verkleWorldState;
    private OverlayWorldState? _overlayWorldState;
    private ITrieStore? _trieStore;
    private IVerkleTreeStore? _verkleTrieStore;

    public WorldStateManager(WorldState worldState, VerkleWorldState verkleWorldState, OverlayWorldState overlayWorldState,
        IDbProvider dbProvider,
        IDbProvider verkleDbProvider,
        ITrieStore trieStore,
        IVerkleTreeStore verkleTrieStore,
        ILogManager logManager) : base(dbProvider, verkleDbProvider,
        trieStore?.AsReadOnly(), verkleTrieStore?.AsReadOnly(new VerkleMemoryDb()), logManager)
    {
        _worldState = worldState;
        _verkleWorldState = verkleWorldState;
        _overlayWorldState = overlayWorldState;
        _trieStore = trieStore;
        _verkleTrieStore = verkleTrieStore;
    }

    public WorldStateManager(WorldState worldState,
        IDbProvider dbProvider,
        ITrieStore trieStore,
        ILogManager logManager) : base(dbProvider,
        trieStore.AsReadOnly(), logManager)
    {
        _worldState = worldState;
        _trieStore = trieStore;
    }

    public WorldStateManager(VerkleWorldState verkleWorldState, IDbProvider verkleDbProvider,
        IVerkleTreeStore verkleTrieStore, ILogManager logManager) : base(verkleDbProvider,
        verkleTrieStore.AsReadOnly(new VerkleMemoryDb()), logManager)
    {
        _verkleWorldState = verkleWorldState;
        _verkleTrieStore = verkleTrieStore;
    }

    public override IWorldState GetGlobalWorldState(Block block)
    {
        return GetGlobalWorldState(block.Timestamp);
    }

    public override IWorldState GetGlobalWorldState(UInt256 blockNumber)
    {
        if (blockNumber < TransitionStartBlockNumber) return _worldState ?? throw new NullReferenceException();
        if (blockNumber < TransitionEndBlockNumber) return _overlayWorldState ?? throw new NullReferenceException();
        return _verkleWorldState ?? throw new NullReferenceException();
    }

    public override IWorldState GetGlobalWorldState(BlockHeader header)
    {
        return GetGlobalWorldState(header.Timestamp);
    }

    public override IWorldState GetGlobalWorldState(ulong timestamp)
    {
        if (timestamp < TransitionStartTimestamp) return _worldState ?? throw new NullReferenceException();
        if (timestamp < TransitionEndTimestamp) return _overlayWorldState ?? throw new NullReferenceException();
        return _verkleWorldState ?? throw new NullReferenceException();
    }

    public override WorldState GetWorldState()
    {
        return _worldState ?? throw new NullReferenceException();
    }
    
    public override VerkleWorldState GetVerkleWorldState()
    {
        return _verkleWorldState ?? throw new NullReferenceException();
    }
    
    public override OverlayWorldState GetOverlayWorldState()
    {
        return _overlayWorldState ?? throw new NullReferenceException();
    }


    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add
        {
            if (_trieStore != null)
            {
                _trieStore.ReorgBoundaryReached += value;
            }

            if (_verkleTrieStore != null)
            {
                _verkleTrieStore.ReorgBoundaryReached += value;
            }
        }
        remove
        {
            if (_trieStore != null)
            {
                _trieStore.ReorgBoundaryReached -= value;
            }

            if (_verkleTrieStore != null)
            {
                _verkleTrieStore.ReorgBoundaryReached -= value;
            }
        }
    }
}