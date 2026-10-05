// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Types;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Test.P2P;

internal static class TestChain
{
    public static (SignedBeaconBlock Anchor, Hash256 AnchorRoot, SignedBeaconBlock[] Blocks) BuildLinkedChain(ulong anchorSlot, params ulong[] slots)
    {
        SignedBeaconBlock anchor = CreateBlock(anchorSlot, Hash256.Zero);
        Hash256 parentRoot = SszRoots.HashTreeRoot(anchor.Message!);
        Hash256 anchorRoot = parentRoot;

        SignedBeaconBlock[] blocks = new SignedBeaconBlock[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            blocks[i] = CreateBlock(slots[i], parentRoot);
            parentRoot = SszRoots.HashTreeRoot(blocks[i].Message!);
        }

        return (anchor, anchorRoot, blocks);
    }

    public static void Persist(BeaconChainStore store, SignedBeaconBlock anchor, Hash256 anchorRoot, IEnumerable<SignedBeaconBlock> blocks)
    {
        store.SetAnchor(anchorRoot, anchor.Message!.Slot);
        store.PutBlock(anchorRoot, anchor);
        store.SetCanonicalRoot(anchor.Message.Slot, anchorRoot);
        foreach (SignedBeaconBlock block in blocks)
        {
            Hash256 root = SszRoots.HashTreeRoot(block.Message!);
            store.PutBlock(root, block);
            store.SetCanonicalRoot(block.Message!.Slot, root);
        }
    }

    public static SignedBeaconBlock CreateBlock(ulong slot, Hash256 parentRoot)
    {
        SignedBeaconBlock block = SignedBeaconBlockBuilders.CreateMinimalBlock(slot);
        block.Message!.ParentRoot = parentRoot;
        ExecutionPayload payload = block.Message.Body!.ExecutionPayload!;
        payload.BlockNumber = slot;
        payload.Timestamp = BeaconChainSpec.Mainnet.GenesisTime + slot * BeaconChainSpec.Mainnet.SecondsPerSlot;
        return block;
    }
}
