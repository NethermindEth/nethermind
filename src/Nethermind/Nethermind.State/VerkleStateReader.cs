// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.TreeStore;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State;

public class VerkleStateReader : IStateReader
{
    private readonly IKeyValueStore _codeDb;
    private readonly ILogger _logger;
    private readonly VerkleStateTree _state;

    public VerkleStateReader(VerkleStateTree verkleTree, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _state = verkleTree;
    }

    public VerkleStateReader(IReadOnlyVerkleTreeStore verkleTree, IKeyValueStore? codeDb, ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _state = new VerkleStateTree(verkleTree, logManager); ;
    }

    public bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account) => TryGetState(stateRoot, address, out account);

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, Address address, in UInt256 index) => _state.Get(address, index, stateRoot);
    public byte[]? GetCode(Hash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString)
        {
            return Array.Empty<byte>();
        }

        return _codeDb[codeHash.Bytes];
    }

    public byte[]? GetCode(in ValueHash256 codeHash)
    {
        if (codeHash == Keccak.OfAnEmptyString)
        {
            return Array.Empty<byte>();
        }

        return _codeDb[codeHash.Bytes];
    }

    public void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
    {
        _state.Accept(treeVisitor, rootHash, visitingOptions);
    }

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        throw new NotImplementedException();
    }

    public bool HasStateForRoot(Hash256 stateRoot)
    {
        return _state.HasStateForStateRoot(stateRoot);
    }

    private bool TryGetState(Hash256 stateRoot, Address address, out AccountStruct account)
    {
        if (stateRoot == Keccak.EmptyTreeHash)
        {
            account = AccountStruct.TotallyEmpty;
            return false;
        }

        Metrics.StateTreeReads++;
        return _state.TryGetStruct(address, out account, stateRoot);
    }
}
