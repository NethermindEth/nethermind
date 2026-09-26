// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Tracing;

/// <summary>Seeds a trace of a block that carries an access list from that list: the state before transaction T is
/// the parent state read through the changes the list records for transactions 0..T-1, so none of them is replayed.
/// Blocks without an access list are left to <paramref name="inner"/>.</summary>
/// <remarks>This is the one place that decides what may seed a block with an access list: the list itself or nothing.
/// It is taken only when it hashes to the header's commitment, and when it is pruned, missing or does not match, the
/// prefix is replayed as it always was; <paramref name="inner"/> is never asked about such a block. Arming costs one
/// lookup in a small cache of validated lists. A miss reads, decodes and hashes the list once, which the header
/// commitment forces, and indexes it in time linear in its size; concurrent misses on one block share that work.</remarks>
public sealed class BlockAccessListPrefixStateSeedSource(IPrefixStateSeedSource inner, IBlockAccessListStore store, ISpecProvider specProvider, ILogManager logManager)
    : IPrefixStateSeedSource
{
    private const int ValidatedBlocks = 4;

    private readonly bool _chainHasAccessLists = specProvider.GetFinalSpec().BlockLevelAccessListsEnabled;
    private readonly ClockCache<ValueHash256, Lazy<BlockAccessListPrefix?>> _validated = new(ValidatedBlocks);
    private readonly Lock _validatedLock = new();
    private readonly ILogger _logger = logManager.GetClassLogger<BlockAccessListPrefixStateSeedSource>();
    private int _reportedCorruption;

    public bool Enabled => _chainHasAccessLists || inner.Enabled;

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
        if (block.Hash is not { } blockHash || block.Header.BlockAccessListHash is null) return false;

        Lazy<BlockAccessListPrefix?> validation = GetOrStartValidation(block, blockHash);
        try
        {
            prefix = validation.Value;
        }
        catch
        {
            Forget(blockHash, validation);
            throw;
        }

        if (prefix is null) Forget(blockHash, validation);
        return prefix is not null;
    }

    private Lazy<BlockAccessListPrefix?> GetOrStartValidation(Block block, Hash256 blockHash)
    {
        using Lock.Scope scope = _validatedLock.EnterScope();
        if (_validated.TryGet(blockHash.ValueHash256, out Lazy<BlockAccessListPrefix?>? validation)) return validation;

        validation = new Lazy<BlockAccessListPrefix?>(() => Validate(block, blockHash), LazyThreadSafetyMode.ExecutionAndPublication);
        _validated.Set(blockHash.ValueHash256, validation);
        return validation;
    }

    /// <summary>A store that failed, or held no list the header commits to, is asked again on the next trace: the list
    /// may be written after the block, and only a validated list is worth keeping.</summary>
    private void Forget(Hash256 blockHash, Lazy<BlockAccessListPrefix?> failed)
    {
        using Lock.Scope scope = _validatedLock.EnterScope();
        if (_validated.TryGet(blockHash.ValueHash256, out Lazy<BlockAccessListPrefix?>? current) && ReferenceEquals(current, failed))
            _validated.Delete(blockHash.ValueHash256);
    }

    private BlockAccessListPrefix? Validate(Block block, Hash256 blockHash)
    {
        Hash256 commitment = block.Header.BlockAccessListHash!;
        ReadOnlyBlockAccessList? accessList = block.BlockAccessList is { WireHash: { } own } carried && own == commitment
            ? carried
            : ReadStored(block, blockHash);

        return accessList?.WireHash == commitment ? new BlockAccessListPrefix(blockHash, accessList, block.Transactions.Length) : null;
    }

    /// <summary>A stored list that does not decode is treated as missing: the block is replayed, which needs no list.</summary>
    private ReadOnlyBlockAccessList? ReadStored(Block block, Hash256 blockHash)
    {
        try
        {
            return store.Get((ulong)block.Number, blockHash);
        }
        catch (RlpException e)
        {
            if (Interlocked.Exchange(ref _reportedCorruption, 1) == 0 && _logger.IsWarn)
                _logger.Warn($"The stored access list of block {block.Number} ({blockHash}) does not decode; its traces replay the transactions ahead of the target. Further such lists are not reported. {e.Message}");
            return null;
        }
    }
}
