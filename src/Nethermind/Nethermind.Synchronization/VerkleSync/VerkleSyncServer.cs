// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncServer
{
    private readonly IVerkleTreeStore _store;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 10000;

    public VerkleSyncServer(IVerkleTreeStore treeStore, ILogManager logManager)
    {
        _store = treeStore ?? throw new ArgumentNullException(nameof(treeStore));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }

    public (List<PathWithSubTree>, VerkleProof) GetSubTreeRanges(Hash256 rootHash, Stem startingStem, Stem? limitStem, long byteLimit, out Banderwagon rootPoint)
    {
        rootPoint = default;
        if(_logger.IsDebug) _logger.Debug($"Getting SubTreeRanges - RH:{rootHash} S:{startingStem} L:{limitStem} Bytes:{byteLimit}");
        var nodes = _store.GetLeafRangeIterator(startingStem, limitStem?? Stem.MaxValue, rootHash, byteLimit).ToList();
        if(_logger.IsDebug) _logger.Debug($"Nodes Count - {nodes.Count}");
        if (nodes.Count == 0) return (new List<PathWithSubTree>(), new VerkleProof());

        VerkleTree tree = new (_store, _logManager);
        VerkleProof vProof = tree.CreateVerkleRangeProof(startingStem.Bytes, nodes[^1].Path.Bytes, out rootPoint, rootHash);
        TestIsGeneratedProofValid(vProof, rootPoint, startingStem, nodes.ToArray());
        return (nodes, vProof);
    }

    private void TestIsGeneratedProofValid(VerkleProof vProof, Banderwagon rootPoint, Stem startingStem, PathWithSubTree[] nodes)
    {
        VerkleTreeStore<PersistEveryBlock>? stateStore = new (new MemDb(), new MemDb(), new MemDb(), LimboLogs.Instance);
        VerkleTree localTree = new VerkleTree(stateStore, LimboLogs.Instance);
        var isCorrect = localTree.CreateStatelessTreeFromRange(vProof, rootPoint, startingStem, nodes[^1].Path, nodes);
        _logger.Info(!isCorrect
            ? $"GetSubTreeRanges: Generated proof is INVALID"
            : $"GetSubTreeRanges: Generated proof is VALID");
    }
}
