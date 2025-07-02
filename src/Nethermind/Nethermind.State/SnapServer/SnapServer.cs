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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.SnapServer;

public class SnapServer : ISnapServer
{
    private readonly IReadOnlyTrieStore _store;
    private readonly TrieStoreWithReadFlags _storeWithReadFlag;
    private readonly IReadOnlyKeyValueStore _codeDb;
    private readonly IStateReader _stateReader;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    // On flatdb/halfpath, using ReadAhead flag significantly reduce IOPs by reading a larger chunk of sequential data.
    // It also skip the block cache, which reduces impact on block processing.
    // On hashdb, this causes each IOP to be significantly larger, so it make it a lot slower than it already is.
    private readonly ReadFlags _optimizedReadFlags = ReadFlags.HintCacheMiss;

    private readonly AccountDecoder _decoder = new AccountDecoder();
    private readonly ILastNStateRootTracker? _lastNStateRootTracker;

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 100000;

    public SnapServer(IReadOnlyTrieStore trieStore, IReadOnlyKeyValueStore codeDb, IStateReader stateReader, ILogManager logManager, ILastNStateRootTracker? lastNStateRootTracker = null)
    {
        _store = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        _stateReader = stateReader;
        _lastNStateRootTracker = lastNStateRootTracker;
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        _logger = logManager.GetClassLogger();

        if (_store.Scheme == INodeStorage.KeyScheme.HalfPath)
        {
            _optimizedReadFlags = ReadFlags.HintReadAhead;
        }
        _storeWithReadFlag = new TrieStoreWithReadFlags(_store.GetTrieStore(null), _optimizedReadFlags);
    }

    private bool IsRootMissing(Hash256 stateRoot)
    {
        return !_stateReader.HasStateForRoot(stateRoot) || _lastNStateRootTracker?.HasStateRoot(stateRoot) == false;
    }

    public IOwnedReadOnlyList<byte[]>? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return ArrayPoolList<byte[]>.Empty();

        if (_logger.IsDebug) _logger.Debug($"Get trie nodes {pathSet.Count}");
        // TODO: use cache to reduce node retrieval from disk
        int pathLength = pathSet.Count;
        ArrayPoolList<byte[]> response = new(pathLength);
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
                        byte[]? rlp = tree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[0]), rootHash);
                        response.Add(rlp);
                    }
                    catch (MissingTrieNodeException)
                    {
                        abort = true;
                    }
                    break;
                default:
                    try
                    {
                        Hash256 storagePath = new Hash256(
                            requestedPath[0].Length == Hash256.Size
                                ? requestedPath[0]
                                : requestedPath[0].PadRight(Hash256.Size));
                        Account? account = GetAccountByPath(tree, rootHash, requestedPath[0]);
                        if (account is not null)
                        {
                            Hash256? storageRoot = account.StorageRoot;
                            StorageTree sTree = new(_store.GetTrieStore(storagePath), storageRoot, _logManager);

                            for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                            {
                                byte[]? sRlp = sTree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[reqStorage]));
                                response.Add(sRlp);
                            }
                        }
                    }
                    catch (MissingTrieNodeException)
                    {
                        abort = true;
                    }
                    break;
            }
        }

        if (response.Count == 0) return ArrayPoolList<byte[]>.Empty();
        return response;
    }

    public IOwnedReadOnlyList<byte[]> GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        long currentByteCount = 0;
        ArrayPoolList<byte[]> response = new(requestedHashes.Count);

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

            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                response.Add([]);
                currentByteCount += 1;
                continue;
            }

            byte[]? code = _codeDb[codeHash.Bytes];
            if (code is not null)
            {
                response.Add(code);
                currentByteCount += code.Length;
            }
        }

        return response;
    }

    public (IOwnedReadOnlyList<PathWithAccount>, IOwnedReadOnlyList<byte[]>) GetAccountRanges(
        Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (ArrayPoolList<PathWithAccount>.Empty(), ArrayPoolList<byte[]>.Empty());
        byteLimit = Math.Max(Math.Min(byteLimit, HardResponseByteLimit), 1);

        AccountCollector accounts = new AccountCollector();
        (long _, IOwnedReadOnlyList<byte[]> proofs, _) = GetNodesFromTrieVisitor(
            rootHash,
            startingHash,
            limitHash?.ToCommitment() ?? Keccak.MaxValue,
            byteLimit,
            null,
            null,
            accounts,
            cancellationToken);

        ArrayPoolList<PathWithAccount> nodes = accounts.Accounts;
        return (nodes, proofs);
    }

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IOwnedReadOnlyList<byte[]>?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), ArrayPoolList<byte[]>.Empty());
        byteLimit = Math.Max(Math.Min(byteLimit, HardResponseByteLimit), 1);

        ValueHash256 startingHash1 = startingHash ?? ValueKeccak.Zero;
        ValueHash256 limitHash1 = limitHash ?? ValueKeccak.MaxValue;
        if (limitHash1 == ValueKeccak.Zero)
        {
            limitHash1 = ValueKeccak.MaxValue;
        }

        long responseSize = 0;
        StateTree tree = startingHash1 == ValueKeccak.Zero
            ? new StateTree(new CachedTrieStore(_storeWithReadFlag), _logManager)
            : new StateTree(_storeWithReadFlag, _logManager);

        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> responseNodes = new(accounts.Count);
        for (int i = 0; i < accounts.Count; i++)
        {
            if (responseSize > byteLimit || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (responseSize > 1 && (Math.Min(byteLimit, HardResponseByteLimit) - responseSize) < 10000)
            {
                break;
            }

            Account accountNeth = GetAccountByPath(tree, rootHash, accounts[i].Path.Bytes.ToArray());
            if (accountNeth is null)
            {
                break;
            }

            Hash256? storagePath = accounts[i].Path.ToCommitment();

            PathWithStorageCollector pathWithStorageCollector = new PathWithStorageCollector();
            (long innerResponseSize, IOwnedReadOnlyList<byte[]> proofs, bool stoppedEarly) = GetNodesFromTrieVisitor(
                rootHash,
                startingHash1,
                limitHash1,
                byteLimit - responseSize,
                storagePath,
                accountNeth.StorageRoot,
                pathWithStorageCollector,
                cancellationToken);

            if (pathWithStorageCollector.Slots.Count == 0)
            {
                //return proof of absence
                return (responseNodes, proofs);
            }

            responseNodes.Add(pathWithStorageCollector.Slots);
            if (stoppedEarly || startingHash1 != Keccak.Zero)
            {
                return (responseNodes, proofs);
            }

            proofs.Dispose();
            responseSize += innerResponseSize;
        }

        return (responseNodes, ArrayPoolList<byte[]>.Empty());
    }

    private (long bytesSize, IOwnedReadOnlyList<byte[]> proofs, bool stoppedEarly) GetNodesFromTrieVisitor(
        in ValueHash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256 limitHash,
        long byteLimit,
        in ValueHash256? storage,
        in ValueHash256? storageRoot,
        RangeQueryVisitor.ILeafValueCollector valueCollector,
        CancellationToken cancellationToken)
    {
        PatriciaTree tree = new(_store, _logManager);
        using RangeQueryVisitor visitor = new(startingHash, limitHash, valueCollector, byteLimit, HardResponseNodeLimit, readFlags: _optimizedReadFlags, cancellationToken);
        VisitingOptions opt = new();
        tree.Accept(visitor, rootHash.ToCommitment(), opt, storageAddr: storage?.ToCommitment(), storageRoot: storageRoot?.ToCommitment());

        ArrayPoolList<byte[]> proofs = startingHash != Keccak.Zero || visitor.StoppedEarly ? visitor.GetProofs() : ArrayPoolList<byte[]>.Empty();
        return (visitor.GetBytesSize(), proofs, visitor.StoppedEarly);
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
            return null;
        }
    }
}
