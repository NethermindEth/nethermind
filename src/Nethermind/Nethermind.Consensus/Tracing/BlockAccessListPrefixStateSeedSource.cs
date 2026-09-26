// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

/// <summary>Seeds a trace of a block that carries an access list from that list: the state before transaction T is
/// the parent state read through the changes the list records for transactions 0..T-1, so none of them is replayed.
/// Blocks without an access list are left to <paramref name="inner"/>.</summary>
/// <remarks>A block with an access list is seeded from it or not at all: the list is taken only when it hashes to the
/// header's commitment, and when it is pruned, missing or does not match, the prefix is replayed as it always was.
/// Arming costs one lookup of the block in a small cache of validated lists; a miss reads, decodes and hashes the list
/// once, which the header commitment forces, and indexes it in time linear in its size.</remarks>
public sealed class BlockAccessListPrefixStateSeedSource(IPrefixStateSeedSource inner, IBlockAccessListStore store, ISpecProvider specProvider)
    : IPrefixStateSeedSource
{
    private const int ValidatedBlocks = 4;

    private readonly bool _chainHasAccessLists = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;
    private readonly ClockCache<ValueHash256, BlockAccessListPrefix> _validated = new(ValidatedBlocks);

    public bool Enabled => _chainHasAccessLists || inner.Enabled;

    /// <summary>What seeds the blocks that carry no access list.</summary>
    internal IPrefixStateSeedSource Fallback => inner;

    public bool SeedsFromBlockAccessLists => true;

    public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
    {
        if (!CarriesAccessList(block)) return inner.TrySeed(block, transactionIndex, slot);
        if (!TryGetPrefix(block, out BlockAccessListPrefix? prefix) || (uint)transactionIndex > (uint)prefix.TransactionCount) return false;

        BlockAccessListReadOverlay overlay = new(prefix) { TransactionIndex = (uint)transactionIndex };
        slot.Arm(overlay, overlay);
        return true;
    }

    public bool TryOpenBlock(Block block, [NotNullWhen(true)] out ICoveredBlock? covered)
    {
        if (!CarriesAccessList(block)) return inner.TryOpenBlock(block, out covered);

        covered = TryGetPrefix(block, out BlockAccessListPrefix? prefix) ? new BlockAccessListCoveredBlock(prefix) : null;
        return covered is not null;
    }

    private bool CarriesAccessList(Block block) => _chainHasAccessLists && specProvider.GetSpec(block.Header).BlockLevelAccessListsEnabled;

    private bool TryGetPrefix(Block block, [NotNullWhen(true)] out BlockAccessListPrefix? prefix)
    {
        prefix = null;
        if (block.Hash is not { } blockHash || block.Header.BlockAccessListHash is not { } commitment) return false;
        if (_validated.TryGet(blockHash.ValueHash256, out prefix)) return true;

        ReadOnlyBlockAccessList? accessList = block.BlockAccessList is { WireHash: { } own } carried && own == commitment
            ? carried
            : store.Get((ulong)block.Number, blockHash);
        if (accessList?.WireHash != commitment) return false;

        prefix = new BlockAccessListPrefix(blockHash, accessList, block.Transactions.Length);
        _validated.Set(blockHash.ValueHash256, prefix);
        return true;
    }
}
