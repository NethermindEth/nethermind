// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Proofs;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleSyncServer
{
    private readonly IVerkleTrieStore _store;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 10000;

    public VerkleSyncServer(IVerkleTrieStore trieStore, ILogManager logManager)
    {
        _store = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }

    public (List<PathWithSubTree>, VerkleProof) GetSubTreeRanges(VerkleCommitment rootHash, Stem startingStem, Stem? limitStem, long byteLimit, out Banderwagon rootPoint)
    {
        rootPoint = default;
        if(_logger.IsDebug) _logger.Debug($"Getting SubTreeRanges - RH:{rootHash} S:{startingStem} L:{limitStem} Bytes:{byteLimit}");
        List<PathWithSubTree> nodes = _store.GetLeafRangeIterator(startingStem, limitStem?? Stem.MaxValue, rootHash, byteLimit).ToList();
        if(_logger.IsDebug) _logger.Debug($"Nodes Count - {nodes.Count}");
        if (nodes.Count == 0) return (new List<PathWithSubTree>(), new VerkleProof());

        VerkleTree tree = new (_store, _logManager);
        VerkleProof vProof =
            tree.CreateVerkleRangeProof(startingStem.Bytes, nodes[^1].Path.Bytes, out rootPoint);
        return (nodes, vProof);
    }
}
