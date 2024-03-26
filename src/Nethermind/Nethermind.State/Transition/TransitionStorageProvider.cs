// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Logging;

namespace Nethermind.State.Transition;

internal class TransitionStorageProvider(
    StateReader merkleStateReader,
    Hash256 finalizedStateRoot,
    VerkleStateTree tree,
    ILogManager? logManager)
    : VerklePersistentStorageProvider(tree, logManager)
{
    private Hash256 FinalizedMerkleStateRoot { get; } = finalizedStateRoot;

    protected override ReadOnlySpan<byte>  LoadFromTree(StorageCell storageCell)
    {
        Db.Metrics.StorageTreeReads++;
        Hash256 key = AccountHeader.GetTreeKeyForStorageSlot(storageCell.Address.Bytes, storageCell.Index);
        byte[]  value = _verkleTree.Get(key) ?? merkleStateReader.GetStorage(FinalizedMerkleStateRoot, storageCell.Address, storageCell.Index).ToArray();
        PushToRegistryOnly(storageCell, value);
        return value;
    }

}
