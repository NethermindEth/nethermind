//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class SnapServer : ISnapServer
{
    private readonly ITrieStore _store;
    private readonly IDbProvider _dbProvider;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private readonly AccountDecoder _decoder = new();

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 10000;


    public SnapServer(IDbProvider dbProvider, ILogManager logManager)
    {
        _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        _store = new TrieStore(
            _dbProvider.StateDb,
            logManager);

        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }


    public byte[][]? GetTrieNodes(PathGroup[] pathSet, Keccak rootHash)
    {
        _logger.Info("GetTrieNodes");
        // TODO: use cache to reduce node retrieval from disk
        int pathLength = pathSet.Length;
        List<byte[]> response = new();
        StateTree tree = new(_store, _logManager);

        for (int reqi = 0; reqi < pathLength; reqi++)
        {
            byte[][]? requestedPath = pathSet[reqi].Group;
            switch (requestedPath.Length)
            {
                case 0:
                    return null;
                case 1:
                    byte[]? rlp = tree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[0]), rootHash);
                    response.Add(rlp);
                    break;
                default:
                    Account? account = GetAccountByPath(tree, rootHash, requestedPath[0]);
                    if (account is null)
                    {
                        break;
                    }
                    Keccak? storageRoot = account.StorageRoot;
                    if (storageRoot.Bytes.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                    {
                        break;
                    }
                    StorageTree sTree = new(_store, storageRoot, _logManager);

                    for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                    {
                        byte[]? sRlp = sTree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[reqStorage]));
                        response.Add(sRlp);
                    }
                    break;
            }
        }

        return response.ToArray();
    }

    public byte[][] GetByteCodes(Keccak[] requestedHashes, long byteLimit)
    {
        _logger.Info("GetByteCodes");
        long currentByteCount = 0;
        List<byte[]> response = new();

        if (byteLimit > HardResponseByteLimit)
        {
            byteLimit = HardResponseByteLimit;
        }

        foreach (Keccak codeHash in requestedHashes)
        {
            // break when the response size exceeds the byteLimit - it is a soft limit
            // so not a big issue if we so over slightly.
            if (currentByteCount > byteLimit)
            {
                break;
            }

            // TODO: handle empty code in the DB itself?
            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                response.Add(new byte[] { });
            }
            byte[]? code = _dbProvider.CodeDb.Get(codeHash);
            if (code is null) continue;
            response.Add(code);
            currentByteCount += code.Length;
        }

        return response.ToArray();
    }

    public (PathWithAccount[], byte[][]) GetAccountRanges(Keccak rootHash, Keccak startingHash, Keccak? limitHash, long byteLimit)
    {
        _logger.Info("GetAccountRanges");

        (Dictionary<byte[], byte[]>? requiredNodes, long _, bool _) = GetNodesFromTrieVisitor(rootHash, startingHash,
            limitHash ?? Keccak.MaxValue, byteLimit, HardResponseByteLimit, isStorage: false);
        StateTree tree = new(_store, _logManager);

        PathWithAccount[] nodes = new PathWithAccount[requiredNodes.Count];
        int index = 0;
        foreach (PathWithAccount? result in requiredNodes.Select(res => new PathWithAccount(new Keccak(res.Key), _decoder.Decode(new RlpStream(res.Value)))))
        {
            nodes[index] = result;
            index += 1;
        }

        if (nodes.Length == 0) return (nodes, Array.Empty<byte[]>());
        byte[][]? proofs = GenerateRangeProof(tree, startingHash, nodes[^1].Path, rootHash);
        return (nodes, proofs);
    }

    public (PathWithStorageSlot[][], byte[][]?) GetStorageRanges(Keccak rootHash, PathWithAccount[] accounts, Keccak? startingHash, Keccak? limitHash, long byteLimit)
    {
        _logger.Info("GetStorageRanges");
        long responseSize = 0;
        StateTree tree = new(_store, _logManager);
        List<PathWithStorageSlot[]> responseNodes = new();
        for (int i = 0; i < accounts.Length; i++)
        {
            if (responseSize > byteLimit)
            {
                break;
            }
            Account accountNeth = GetAccountByPath(tree, rootHash, accounts[i].Path.Bytes);
            if (accountNeth is null)
            {
                return (responseNodes.ToArray(), null);
            }
            Keccak? storageRoot = accountNeth.StorageRoot;

            startingHash = startingHash == null ? Keccak.Zero : startingHash;
            limitHash = limitHash == null ? Keccak.MaxValue : limitHash;

            (Dictionary<byte[], byte[]>? requiredNodes, long innerResponseSize, bool stopped) = GetNodesFromTrieVisitor(storageRoot,
                startingHash, limitHash, byteLimit - responseSize, HardResponseByteLimit - responseSize, true);

            PathWithStorageSlot[] nodes = new PathWithStorageSlot[requiredNodes.Count];
            int index = 0;
            foreach (PathWithStorageSlot? result in requiredNodes.Select(res => new PathWithStorageSlot(new Keccak(res.Key), res.Value)))
            {
                nodes[index] = result;
                index += 1;
            }
            responseNodes.Add(nodes);
            if (stopped || startingHash != Keccak.Zero)
            {
                byte[][]? proofs = GenerateRangeProof(tree, startingHash, nodes[^1].Path, storageRoot);
                return (responseNodes.ToArray(), proofs);
            }
            responseSize += innerResponseSize;
        }
        return (responseNodes.ToArray(), null);
    }

    private (Dictionary<byte[], byte[]>?, long, bool) GetNodesFromTrieVisitor(Keccak rootHash, Keccak startingHash, Keccak limitHash,
        long byteLimit, long hardByteLimit, bool isStorage = false)
    {
        PatriciaTree tree = new(_store, _logManager);

        RangeQueryVisitor visitor = new(startingHash.Bytes, limitHash.Bytes, !isStorage, byteLimit, hardByteLimit, HardResponseNodeLimit);
        VisitingOptions opt = new() { ExpectAccounts = false, KeepTrackOfAbsolutePath = true };
        tree.Accept(visitor, rootHash, opt);
        (Dictionary<byte[], byte[]>? requiredNodes, long responseSize) = visitor.GetNodesAndSize();

        return (requiredNodes, responseSize, visitor._isStoppedDueToHardLimit);
    }

    private Account? GetAccountByPath(StateTree tree, Keccak rootHash, byte[] accountPath)
    {
        _logger.Info("GetAccountByPath");
        byte[]? bytes = tree.Get(accountPath, rootHash);
        return bytes is null ? null : _decoder.Decode(bytes.AsRlpStream());
    }

    private byte[][] GenerateRangeProof(PatriciaTree tree, Keccak start, Keccak end, Keccak rootHash)
    {
        VisitingOptions opt = new() { ExpectAccounts = false };
        ProofCollector accountProofCollector = new(start.Bytes);
        tree.Accept(accountProofCollector, rootHash, opt);
        byte[][]? firstProof = accountProofCollector.BuildResult();

        accountProofCollector = new ProofCollector(end.Bytes);
        tree.Accept(accountProofCollector, rootHash, opt);
        byte[][]? lastProof = accountProofCollector.BuildResult();

        HashSet<byte[]> proofs = new();
        proofs.AddRange(firstProof);
        proofs.AddRange(lastProof);
        return proofs.ToArray();
    }
}
