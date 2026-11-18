// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync.Snap;

public class SnapFlatStateServer(
    IFlatDbManager flatDbManager,
    IFlatStateRootIndex stateRootIndex,
    ILogManager logManager) : ISnapStateServer
{
    public bool CanServe => true;

    private readonly ILogger _logger = logManager.GetClassLogger<SnapFlatStateServer>();

    // Range walks visit trie nodes in path order, which is the key order of the flat trie columns, so on a
    // FullScan reader they are served by readahead iterators instead of a point get per node, as on HalfPath.
    // Those iterators do not fill the block cache; HintCacheMiss keeps their point-get fallback out of it too.
    private const ReadFlags RangeReadFlags = ReadFlags.HintReadAhead | ReadFlags.HintCacheMiss;

    private bool TryGetBundle(Hash256 rootHash, ReaderFlags readerFlags, out ReadOnlySnapshotBundle bundle, out StateId stateId)
    {
        if (!stateRootIndex.TryGetStateId(rootHash, out stateId))
        {
            bundle = null!;
            return false;
        }

        try
        {
            bundle = flatDbManager.GatherReadOnlySnapshotBundle(stateId, readerFlags);
            return true;
        }
        catch (StateUnavailableException)
        {
            bundle = null!;
            return false;
        }
    }

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        GetTrieNodes(pathSet, rootHash, long.MaxValue, cancellationToken);

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit, CancellationToken cancellationToken)
    {
        if (!TryGetBundle(rootHash, ReaderFlags.None, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return EmptyByteArrayList.Instance;

        using (bundle)
        {
            if (_logger.IsDebug) _logger.Debug($"Get trie nodes {pathSet.Count}");

            byteLimit = Math.Max(Math.Min(byteLimit, ISnapStateServer.HardResponseByteLimit), 1);
            int pathLength = pathSet.Count;
            using DeferredRlpItemList.Builder builder = new(pathLength);
            DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();
            ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
            // Each path is walked from the root, so the request shares the nodes it resolved instead of reading them again.
            StateTree tree = new(new CachedTrieStore(trieStore), logManager);
            Hash256 stateRoot = stateId.StateRoot.ToCommitment();
            bool abort = false;
            long responseSize = 0;
            int lookups = 0;

            for (int i = 0; i < pathLength && !abort && responseSize < byteLimit && lookups < ISnapStateServer.MaxTrieNodeLookups && !cancellationToken.IsCancellationRequested; i++)
            {
                byte[][]? requestedPath = pathSet[i].Group;
                switch (requestedPath.Length)
                {
                    case 0:
                        return null;
                    case 1:
                        try
                        {
                            // A path no node can sit at is answered like a missing node: with an empty slot.
                            byte[]? rlp = Nibbles.TryCompactToNodePath(requestedPath[0], out byte[]? nibbles)
                                ? tree.GetNodeByPath(nibbles, stateRoot)
                                : null;
                            lookups++;
                            writer.WriteValue(rlp);
                            responseSize += rlp?.Length ?? 0;
                        }
                        catch (MissingTrieNodeException)
                        {
                            abort = true;
                        }
                        break;
                    default:
                        try
                        {
                            byte[] accountPathBytes = requestedPath[0];
                            // A key longer than a hash names no account, so its group is answered like a missing account's.
                            Hash256? storageRoot = accountPathBytes.Length <= Hash256.Size
                                ? GetStorageRootByPath(tree, stateRoot, accountPathBytes)
                                : null;
                            lookups++;
                            if (storageRoot is not null)
                            {
                                Hash256 storagePath = new(
                                    accountPathBytes.Length == Hash256.Size
                                        ? accountPathBytes
                                        : accountPathBytes.PadRight(Hash256.Size));
                                StorageTree sTree = new(trieStore.GetStorageTrieStore(storagePath), storageRoot, logManager);

                                for (int reqStorage = 1; reqStorage < requestedPath.Length && responseSize < byteLimit && lookups < ISnapStateServer.MaxTrieNodeLookups && !cancellationToken.IsCancellationRequested; reqStorage++)
                                {
                                    byte[]? sRlp = Nibbles.TryCompactToNodePath(requestedPath[reqStorage], out byte[]? storageNibbles)
                                        ? sTree.GetNodeByPath(storageNibbles)
                                        : null;
                                    lookups++;
                                    writer.WriteValue(sRlp);
                                    responseSize += sRlp?.Length ?? 0;
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

            writer.Dispose();
            return new RlpByteArrayList(builder.ToRlpItemList());
        }
    }

    public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(
        Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        if (!TryGetBundle(rootHash, ReaderFlags.FullScan, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return (ArrayPoolList<PathWithAccount>.Empty(), EmptyByteArrayList.Instance);

        using (bundle)
        {
            byteLimit = Math.Max(Math.Min(byteLimit, ISnapStateServer.HardResponseByteLimit), 1);

            AccountCollector accounts = new(byteLimit);
            try
            {
                (long _, IByteArrayList proofs, _) = GetNodesFromTrieVisitor(
                    bundle,
                    stateId.StateRoot.ToCommitment(),
                    startingHash,
                    limitHash ?? ValueKeccak.MaxValue,
                    byteLimit,
                    null,
                    null,
                    accounts,
                    cancellationToken);

                EncodedSnapEntries<PathWithAccount> nodes = accounts.Accounts;
                return (nodes, proofs);
            }
            catch
            {
                accounts.Accounts.Dispose();
                throw;
            }
        }
    }

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        if (!TryGetBundle(rootHash, ReaderFlags.FullScan, out ReadOnlySnapshotBundle bundle, out StateId stateId))
            return (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), EmptyByteArrayList.Instance);

        using (bundle)
        {
            byteLimit = Math.Max(Math.Min(byteLimit, ISnapStateServer.HardResponseByteLimit), 1);

            ValueHash256 startingHash1 = startingHash ?? ValueKeccak.Zero;
            ValueHash256 limitHash1 = limitHash ?? ValueKeccak.MaxValue;
            if (limitHash1 == ValueKeccak.Zero)
            {
                limitHash1 = ValueKeccak.MaxValue;
            }

            long responseSize = 0;
            ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);
            Hash256 stateRoot = stateId.StateRoot.ToCommitment();
            StateTree tree = startingHash1 == ValueKeccak.Zero
                ? new StateTree(new CachedTrieStore(trieStore), logManager)
                : new StateTree(trieStore, logManager);

            EncodedStorageRanges responseNodes = new(accounts.Count);
            try
            {
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

                    Hash256? storageRoot = GetStorageRootByPath(tree, stateRoot, accounts[i].Path.Bytes);
                    if (storageRoot is null)
                    {
                        break;
                    }

                    Hash256 storagePath = accounts[i].Path.ToCommitment();

                    (long innerResponseSize, IByteArrayList proofs, bool stoppedEarly) = GetNodesFromTrieVisitor(
                        bundle,
                        stateRoot,
                        startingHash1,
                        limitHash1,
                        byteLimit - responseSize,
                        storagePath,
                        storageRoot,
                        responseNodes.Collector,
                        cancellationToken);

                    if (!responseNodes.TryCompleteAccount())
                    {
                        // return proof of absence
                        return (responseNodes, proofs);
                    }

                    if (stoppedEarly || startingHash1 != Keccak.Zero)
                    {
                        return (responseNodes, proofs);
                    }

                    proofs.Dispose();
                    responseSize += innerResponseSize;
                }
            }
            catch
            {
                responseNodes.Dispose();
                throw;
            }

            return (responseNodes, EmptyByteArrayList.Instance);
        }
    }

    private static (long bytesSize, IByteArrayList proofs, bool stoppedEarly) GetNodesFromTrieVisitor(
        ReadOnlySnapshotBundle bundle,
        Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256 limitHash,
        long byteLimit,
        Hash256? storage,
        Hash256? storageRoot,
        RangeQueryVisitor.ILeafValueCollector valueCollector,
        CancellationToken cancellationToken)
    {
        using RangeQueryVisitor visitor = new(startingHash, limitHash, valueCollector, byteLimit, ISnapStateServer.HardResponseNodeLimit, readFlags: RangeReadFlags, cancellationToken);
        new NodeRecordBufferVisitor<TreePathContext>(bundle, bundle.StateSnapshot, visitor, VisitingOptions.Default, null)
            .Accept(storageRoot ?? rootHash, storage);

        IByteArrayList proofs = startingHash != Keccak.Zero || visitor.StoppedEarly ? new ByteArrayListAdapter(visitor.GetProofs()) : EmptyByteArrayList.Instance;
        return (visitor.GetBytesSize(), proofs, visitor.StoppedEarly);
    }

    private static Hash256? GetStorageRootByPath(StateTree tree, Hash256 rootHash, ReadOnlySpan<byte> accountPath)
    {
        try
        {
            ReadOnlySpan<byte> bytes = tree.Get(accountPath, rootHash);
            if (bytes.IsNullOrEmpty())
            {
                return null;
            }

            try
            {
                if (!AccountDecoder.Instance.TryDecodeStruct(bytes, out AccountStruct account))
                {
                    return null;
                }

                return account.HasStorage ? account.StorageRoot.ToCommitment() : Keccak.EmptyTreeHash;
            }
            catch (Exception exception) when (exception is RlpReaderException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                return AccountDecoder.Instance.Decode(bytes)?.StorageRoot;
            }
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
