// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.Sync;

public class FlatSnapServer(
    IFlatDbManager flatDbManager,
    IReadOnlyKeyValueStore codeDb,
    IFlatStateRootIndex stateRootIndex,
    ILogManager logManager) : ISnapServer
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly AccountDecoder _decoder = new();

    private const long HardResponseByteLimit = 2000000;
    private const int HardResponseNodeLimit = 100000;

    // Flat state uses HintCacheMiss since it has different I/O patterns than Patricia
    private readonly ReadFlags _optimizedReadFlags = ReadFlags.HintCacheMiss;

    private bool TryGetBundle(Hash256 rootHash, out ReadOnlySnapshotBundle bundle, out StateId stateId)
    {
        if (!stateRootIndex.TryGetStateId(rootHash, out stateId))
        {
            bundle = null!;
            return false;
        }

        bundle = flatDbManager.GatherReadOnlySnapshotBundle(stateId);
        return true;
    }

    public IOwnedReadOnlyList<byte[]>? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken)
    {
        if (!TryGetBundle(rootHash, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return ArrayPoolList<byte[]>.Empty();

        using (bundle)
        {
            if (_logger.IsDebug) _logger.Debug($"Get trie nodes {pathSet.Count}");

            int pathLength = pathSet.Count;
            ArrayPoolList<byte[]> response = new(pathLength);
            ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
            StateTree tree = new(trieStore, logManager);
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
                            byte[]? rlp = tree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[0]), stateId.StateRoot.ToCommitment());
                            if (rlp is not null)
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
                            Hash256 storagePath = new(
                                requestedPath[0].Length == Hash256.Size
                                    ? requestedPath[0]
                                    : requestedPath[0].PadRight(Hash256.Size));
                            Account? account = GetAccountByPath(tree, stateId.StateRoot.ToCommitment(), requestedPath[0]);
                            if (account is not null)
                            {
                                Hash256? storageRoot = account.StorageRoot;
                                StorageTree sTree = new(trieStore.GetStorageTrieStore(storagePath), storageRoot, logManager);

                                for (int reqStorage = 1; reqStorage < requestedPath.Length; reqStorage++)
                                {
                                    byte[]? sRlp = sTree.GetNodeByPath(Nibbles.CompactToHexEncode(requestedPath[reqStorage]));
                                    if (sRlp is not null)
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

            byte[]? code = codeDb[codeHash.Bytes];
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
        if (!TryGetBundle(rootHash, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return (ArrayPoolList<PathWithAccount>.Empty(), ArrayPoolList<byte[]>.Empty());

        using (bundle)
        {
            byteLimit = Math.Max(Math.Min(byteLimit, HardResponseByteLimit), 1);

            AccountCollector accounts = new();
            (long _, IOwnedReadOnlyList<byte[]> proofs, _) = GetNodesFromTrieVisitor(
                bundle,
                stateId.StateRoot,
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
    }

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IOwnedReadOnlyList<byte[]>?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        if (!TryGetBundle(rootHash, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), ArrayPoolList<byte[]>.Empty());

        using (bundle)
        {
            byteLimit = Math.Max(Math.Min(byteLimit, HardResponseByteLimit), 1);

            ValueHash256 startingHash1 = startingHash ?? ValueKeccak.Zero;
            ValueHash256 limitHash1 = limitHash ?? ValueKeccak.MaxValue;
            if (limitHash1 == ValueKeccak.Zero)
            {
                limitHash1 = ValueKeccak.MaxValue;
            }

            long responseSize = 0;
            ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
            StateTree tree = startingHash1 == ValueKeccak.Zero
                ? new StateTree(new CachedTrieStore(trieStore), logManager)
                : new StateTree(trieStore, logManager);

            ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> responseNodes = new(accounts.Count);
            for (int i = 0; i < accounts.Count; i++)
            {
                if (responseSize > byteLimit || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (responseSize > 1 && (byteLimit - responseSize) < 10000)
                {
                    break;
                }

                Account? accountNeth = GetAccountByPath(tree, stateId.StateRoot.ToCommitment(), accounts[i].Path.Bytes.ToArray());
                if (accountNeth is null)
                {
                    break;
                }

                Hash256? storagePath = accounts[i].Path.ToCommitment();

                PathWithStorageCollector pathWithStorageCollector = new();
                (long innerResponseSize, IOwnedReadOnlyList<byte[]> proofs, bool stoppedEarly) = GetNodesFromTrieVisitor(
                    bundle,
                    stateId.StateRoot,
                    startingHash1,
                    limitHash1,
                    byteLimit - responseSize,
                    storagePath,
                    accountNeth.StorageRoot,
                    pathWithStorageCollector,
                    cancellationToken);

                if (pathWithStorageCollector.Slots.Count == 0)
                {
                    // return proof of absence
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
    }

    private (long bytesSize, IOwnedReadOnlyList<byte[]> proofs, bool stoppedEarly) GetNodesFromTrieVisitor(
        ReadOnlySnapshotBundle bundle,
        in ValueHash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256 limitHash,
        long byteLimit,
        in ValueHash256? storage,
        in ValueHash256? storageRoot,
        RangeQueryVisitor.ILeafValueCollector valueCollector,
        CancellationToken cancellationToken)
    {
        ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
        PatriciaTree tree = new(trieStore, logManager);
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
            Rlp.ValueDecoderContext rlpContext = new(bytes);
            return bytes.IsNullOrEmpty() ? null : _decoder.Decode(ref rlpContext);
        }
        catch (TrieNodeException)
        {
            return null;
        }
        catch (MissingTrieNodeException)
        {
            return null;
        }
    }
}
