// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class FlatStateReader(
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ReadonlyReaderRepository readonlyReaderRepositor,
    IFlatDiffRepository flatDiffRepository,
    ILogManager  logManager
): IStateReader
{
    public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
    {
        using RefCountingDisposableBox<SnapshotBundle> readerBox = readonlyReaderRepositor.GatherReadOnlyReaderAtBaseBlock(new StateId(baseBlock));
        if (readerBox is null)
        {
            account = default;
            return false;
        }

        SnapshotBundle reader = readerBox.Item;
        if (reader.TryGetAccount(address, out Account? accountCls) && accountCls != null)
        {
            account = accountCls.ToStruct();
            return true;
        }

        account = default;
        return false;
    }

    // TODO: Why is it return span? How is it suppose to dispose itself?
    public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
    {
        using RefCountingDisposableBox<SnapshotBundle> readerBox = readonlyReaderRepositor.GatherReadOnlyReaderAtBaseBlock(new StateId(baseBlock));
        if (readerBox is null)
        {
            return Array.Empty<byte>();
        }

        SnapshotBundle reader = readerBox.Item;
        if (reader.TryGetSlot(address, index, reader.DetermineSelfDestructSnapshotIdx(address), out byte[] value))
        {
            return value;
        }

        return Array.Empty<byte>();
    }

    public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : codeDb[codeHash.Bytes];

    public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : codeDb[codeHash.Bytes];

    public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
    {
        StateId? stateId = flatDiffRepository.FindStateIdForStateRoot(stateRoot);
        if (stateId is null)
        {
            throw new InvalidOperationException($"State root {stateRoot} not found");
        }

        using RefCountingDisposableBox<SnapshotBundle> readerBox = readonlyReaderRepositor.GatherReadOnlyReaderAtBaseBlock(stateId.Value);
        if (readerBox is null)
        {
            throw new InvalidOperationException($"State root {stateRoot} not found");
        }

        SnapshotBundle reader = readerBox.Item;
        StateTrieStoreAdapter trieStoreAdapter = new StateTrieStoreAdapter(
            reader,
            new ConcurrencyQuota(),
            false);

        PatriciaTree patriciaTree = new PatriciaTree(trieStoreAdapter, logManager);
        patriciaTree.Accept(treeVisitor, stateRoot, visitingOptions);
    }

    public bool HasStateForBlock(BlockHeader? baseBlock)
    {
        return flatDiffRepository.HasStateForBlock(new StateId(baseBlock));
    }
}
