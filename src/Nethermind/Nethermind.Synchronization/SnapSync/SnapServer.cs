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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    private readonly IReadOnlyTrieStore _store;
    private readonly TrieStoreWithReadFlags _storeWithReadFlag;
    private readonly IReadOnlyKeyValueStore _codeDb;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    // On flatdb/halfpath, using ReadAhead flag significantly reduce IOPs by reading a larger chunk of sequential data.
    // It also skip the block cache, which reduces impact on block processing.
    // On hashdb, this causes each IOP to be significantly larger, so it make it a lot slower than it already is.
    private readonly ReadFlags _optimizedReadFlags = ReadFlags.HintCacheMiss;

    // Some cache to make sure we don't accidentally serve blocks with missing trie node.
    // We don't know for sure if a tree was pruned with just the root hash.
    private readonly LruCache<ValueHash256, DateTimeOffset> _rootWithMissingNode = new LruCache<ValueHash256, DateTimeOffset>(256, "");

    private readonly AccountDecoder _decoder = new AccountDecoder();

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 100000;

    public SnapServer(IReadOnlyTrieStore trieStore, IReadOnlyKeyValueStore codeDb, ILogManager logManager)
    {
        _store = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _storeWithReadFlag = new TrieStoreWithReadFlags(_store, _optimizedReadFlags);
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();
    }

    private bool IsRootMissing(in ValueHash256 rootHash)
    {
        // TODO: Before checking missing node cache, actually check StateReader.
        if (_rootWithMissingNode.TryGet(rootHash, out DateTimeOffset missingTime))
        {
            if (DateTimeOffset.Now - missingTime < TimeSpan.FromSeconds(10))
            {
                return true;
            }

            // So, problem is, it could be that the missing root was just not processed, so we can't just not retry
            // forever. `DateTimeOffset.Now` is probably heavy though.
            _rootWithMissingNode.Delete(rootHash);
        }

        return false;
    }

    private void TrackTrieNodeException(in ValueHash256 rootHash)
    {
        _rootWithMissingNode.Set(rootHash, DateTimeOffset.Now);
    }

    public byte[][]? GetTrieNodes(PathGroup[] pathSet, in ValueHash256 rootHash, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return Array.Empty<byte[]>();

        if (_logger.IsDebug) _logger.Debug($"Get trie nodes {pathSet.Length}");
        // TODO: use cache to reduce node retrieval from disk
        int pathLength = pathSet.Length;
        using ArrayPoolList<byte[]> response = new(pathLength);
        StateTree tree = new(_store, _logManager);
        bool abort = false;

        for (int i = 0; i < pathLength && !abort && !cancellationToken.IsCancellationRequested; i++)
        {
            byte[][]? requestedPath = pathSet[i].Group;
            switch (requestedPath.Length)
            {
                case 0:
                    return null;
                case 1:
                    try
                    {
                        byte[]? rlp = tree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[0]), rootHash.ToCommitment());
                        response.Add(rlp);
                    }
                    catch (MissingTrieNodeException)
                    {
                        TrackTrieNodeException(rootHash);
                        abort = true;
                    }
                    break;
                default:
                    try
                    {
                        Account? account = GetAccountByPath(tree, rootHash, requestedPath[0]);
                        if (account is not null)
                        {
                            Hash256? storageRoot = account.StorageRoot;
                            if (!storageRoot.Bytes.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                            {
                                StorageTree sTree = new(_store, storageRoot, _logManager);

                                for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                                {
                                    byte[]? sRlp = sTree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[reqStorage]));
                                    response.Add(sRlp);
                                }
                            }
                        }
                    }
                    catch (MissingTrieNodeException)
                    {
                        TrackTrieNodeException(rootHash);
                        abort = true;
                    }
                    break;
            }
        }
        return response.ToArray();
    }

    public byte[][] GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        long currentByteCount = 0;
        using ArrayPoolList<byte[]> response = new(requestedHashes.Count);

        if (byteLimit > HardResponseByteLimit)
        {
            byteLimit = HardResponseByteLimit;
        }

        foreach (ValueHash256 codeHash in requestedHashes)
        {
            // break when the response size exceeds the byteLimit - it is a soft limit
            // so not a big issue if we so over slightly.
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // TODO: handle empty code in the DB itself?
            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                response.Add(Array.Empty<byte>());
            }

            byte[]? code = _codeDb[codeHash.Bytes];
            if (code is not null)
            {
                response.Add(code);
                currentByteCount += code.Length;
            }
        }

        return response.ToArray();
    }

    public (PathWithAccount[], byte[][]) GetAccountRanges(in ValueHash256 rootHash, in ValueHash256 startingHash, in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (Array.Empty<PathWithAccount>(), Array.Empty<byte[]>());

        (IDictionary<ValueHash256, byte[]>? requiredNodes, long _, byte[][] proofs, bool stoppedEarly) = GetNodesFromTrieVisitor(rootHash, startingHash,
            limitHash?.ToCommitment() ?? Keccak.MaxValue, byteLimit, HardResponseByteLimit, null, null, cancellationToken);

        PathWithAccount[] nodes = new PathWithAccount[requiredNodes.Count];
        int index = 0;
        foreach (PathWithAccount? result in requiredNodes.Select(res => new PathWithAccount(res.Key, _decoder.Decode(new RlpStream(res.Value)))))
        {
            nodes[index] = result;
            index += 1;
        }

        return nodes.Length == 0 ? (nodes, Array.Empty<byte[]>()) : (nodes, proofs);
    }

    public (PathWithStorageSlot[][], byte[][]?) GetStorageRanges(in ValueHash256 rootHash, PathWithAccount[] accounts, in ValueHash256? startingHash, in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (Array.Empty<PathWithStorageSlot[]>(), Array.Empty<byte[]>());

        long responseSize = 0;
        StateTree tree = new(_storeWithReadFlag, _logManager);

        ValueHash256 startingHash1 = startingHash ?? ValueKeccak.Zero;
        ValueHash256 limitHash1 = limitHash ?? ValueKeccak.MaxValue;
        if (limitHash1 == ValueKeccak.Zero)
        {
            limitHash1 = ValueKeccak.MaxValue;
        }

        using ArrayPoolList<PathWithStorageSlot[]> responseNodes = new(accounts.Length);
        for (int i = 0; i < accounts.Length; i++)
        {
            if (responseSize > byteLimit || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (responseSize > 1 && (Math.Min(byteLimit, HardResponseByteLimit) - responseSize) < 10000)
            {
                return (responseNodes.ToArray(), Array.Empty<byte[]>());
            }

            Account accountNeth = GetAccountByPath(tree, rootHash, accounts[i].Path.Bytes.ToArray());
            if (accountNeth is null)
            {
                return (responseNodes.ToArray(), Array.Empty<byte[]>());
            }

            Hash256? storagePath = accounts[i].Path.ToCommitment();

            (IDictionary<ValueHash256, byte[]>? requiredNodes, long innerResponseSize, byte[][] proofs, bool stoppedEarly) = GetNodesFromTrieVisitor(
                rootHash,
                startingHash1,
                limitHash1,
                byteLimit - responseSize,
                HardResponseByteLimit - responseSize,
                storagePath,
                accountNeth.StorageRoot,
                cancellationToken);

            if (requiredNodes.Count == 0)
            {
                return (responseNodes.ToArray(), Array.Empty<byte[]>());
            }

            PathWithStorageSlot[] nodes = new PathWithStorageSlot[requiredNodes.Count];
            int index = 0;
            foreach (PathWithStorageSlot? result in requiredNodes.Select(res => new PathWithStorageSlot(res.Key, res.Value)))
            {
                nodes[index] = result.Value;
                index += 1;
            }
            responseNodes.Add(nodes);
            if (stoppedEarly || startingHash1 != Keccak.Zero)
            {
                return (responseNodes.ToArray(), proofs);
            }
            responseSize += innerResponseSize;
        }
        return (responseNodes.ToArray(), Array.Empty<byte[]>());
    }

    private (IDictionary<ValueHash256, byte[]>?, long, byte[][], bool) GetNodesFromTrieVisitor(in ValueHash256 rootHash, in ValueHash256 startingHash, in ValueHash256 limitHash,
        long byteLimit, long hardByteLimit, in ValueHash256? storage, in ValueHash256? storageRoot, CancellationToken cancellationToken)
    {
        bool isStorage = storage is not null;
        PatriciaTree tree = new(_store, _logManager);
        using RangeQueryVisitor visitor = new(startingHash, limitHash, !isStorage, byteLimit, hardByteLimit, HardResponseNodeLimit, readFlags: _optimizedReadFlags, cancellationToken);
        VisitingOptions opt = new() { ExpectAccounts = false };
        tree.Accept(visitor, rootHash.ToCommitment(), opt, storageAddr: storage?.ToCommitment(), storageRoot: storageRoot?.ToCommitment());

        (IDictionary<ValueHash256, byte[]>? requiredNodes, long responseSize) = visitor.GetNodesAndSize();
        byte[][] proofs = Array.Empty<byte[]>();
        if (startingHash != Keccak.Zero || visitor.StoppedEarly) proofs = visitor.GetProofs();

        return (requiredNodes, responseSize, proofs, visitor.StoppedEarly);
    }

    private Account? GetAccountByPath(StateTree tree, in ValueHash256 rootHash, byte[] accountPath)
    {
        try
        {
            ReadOnlySpan<byte> bytes = tree.Get(accountPath, rootHash.ToCommitment());
            Rlp.ValueDecoderContext rlpContext = new Rlp.ValueDecoderContext(bytes);
            return bytes.IsNullOrEmpty() ? null : _decoder.Decode(ref rlpContext);
        }
        catch (MissingTrieNodeException)
        {
            TrackTrieNodeException(rootHash);
            return null;
        }
    }
}
