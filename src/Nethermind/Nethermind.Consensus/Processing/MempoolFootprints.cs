// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;

namespace Nethermind.Consensus.Processing;

/// <summary>The footprints the mempool pass records for the block after <paramref name="head"/>, by transaction hash.</summary>
/// <remarks>
/// A run is recorded on a predicted header, so a footprint holds in the block only where the block context it ran on
/// is the block's. The parent, number, base fee, blob gas and slot are the block's when the parent is; the timestamp and
/// the gas limit are only checked when the run read them; the coinbase and the random value are never known, so a run
/// that read either is not used, nor is one that ran out of gas anywhere, which may have been charging for the
/// coinbase. The predicted coinbase is <see cref="Coinbase"/>, an address no transaction can name
/// without reading it: the fee it is paid becomes the block coinbase's, and a run that touched the block's coinbase,
/// which execution warms (EIP-3651), is not used.
/// </remarks>
internal sealed class MempoolFootprints(BlockHeader head)
{
    private readonly Hash256? _parentHash = head.Hash;
    private readonly ulong _number = head.Number + 1;
    /// <summary>Runs one session keeps; a gap long enough to fill it warms transactions no single block takes.</summary>
    internal const int MaxEntries = 8192;

    private readonly ConcurrentDictionary<Hash256, Entry> _entries = new();
    private int _count;

    /// <summary>The coinbase of the headers the runs are recorded on.</summary>
    public Address Coinbase { get; } = new(RandomNumberGenerator.GetBytes(Address.Size));

    public int Count => Volatile.Read(ref _count);

    /// <param name="header">The predicted header the run executed on.</param>
    public void Record(TransactionFootprint footprint, BlockHeader header, IReleaseSpec spec)
    {
        if (footprint.Transaction.Hash is not { } hash) return;
        Entry entry = new(footprint, header, spec);
        if (_entries.ContainsKey(hash)) _entries[hash] = entry;
        else if (Volatile.Read(ref _count) < MaxEntries && _entries.TryAdd(hash, entry)) Interlocked.Increment(ref _count);
    }

    /// <summary>Stores the footprints that hold in <paramref name="block"/> at their transactions' positions.</summary>
    /// <returns>The hashes of the transactions stored, or <see langword="null"/> when none is.</returns>
    public HashSet<Hash256>? Seed(Block block, IReleaseSpec spec, BlockFootprints footprints)
    {
        BlockHeader header = block.Header;
        if (_entries.IsEmpty || _parentHash is null || header.ParentHash != _parentHash || header.Number != _number
            || header.GasBeneficiary is not { } coinbase)
        {
            return null;
        }

        HashSet<Hash256>? seeded = null;
        Transaction[] transactions = block.Transactions;
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            // A recorded run's transaction was recordable, and the block's has its hash, so its content and sender.
            if (tx.Hash is not { } hash || !_entries.TryGetValue(hash, out Entry entry)) continue;
            if (ForBlock(in entry, tx, header, spec, coinbase) is not { } footprint) continue;
            footprints.Store(i, footprint);
            (seeded ??= []).Add(hash);
        }

        return seeded;
    }

    private TransactionFootprint? ForBlock(in Entry entry, Transaction tx, BlockHeader header, IReleaseSpec spec, Address coinbase)
    {
        TransactionFootprint footprint = entry.Footprint;
        BlockHeader predicted = entry.Header;
        BlockContextReads reads = footprint.ContextReads;
        if (!ReferenceEquals(entry.Spec, spec)
            || (reads & (BlockContextReads.Coinbase | BlockContextReads.PrevRandao | BlockContextReads.OutOfGas)) != 0
            || ((reads & BlockContextReads.Timestamp) != 0 && predicted.Timestamp != header.Timestamp)
            || ((reads & BlockContextReads.GasLimit) != 0 && predicted.GasLimit != header.GasLimit)
            || predicted.BaseFeePerGas != header.BaseFeePerGas
            || predicted.ExcessBlobGas != header.ExcessBlobGas
            || predicted.SlotNumber != header.SlotNumber)
        {
            return null;
        }

        // A dependence on either coinbase is a dependence on which one it is.
        foreach (ref readonly AccountPrecondition account in footprint.Accounts)
        {
            if (account.Address == Coinbase || account.Address == coinbase) return null;
        }

        foreach (ref readonly SlotPrecondition slot in footprint.Slots)
        {
            if (slot.Cell.Address == Coinbase || slot.Cell.Address == coinbase) return null;
        }

        // The fee is the one change to the predicted coinbase: credited whether or not the account exists.
        StateEffect[] effects = footprint.Effects.ToArray();
        int fees = 0;
        for (int i = 0; i < effects.Length; i++)
        {
            ref StateEffect effect = ref effects[i];
            Address address = effect.Address;
            if (address == coinbase) return null;
            if (address != Coinbase) continue;
            if (effect.Kind != EffectKind.AddToBalanceAndCreateIfNotExists) return null;
            fees++;
            effect.Cell = new StorageCell(coinbase, effect.Cell.Index);
        }

        return fees == 1 ? footprint.For(tx, effects) : null;
    }

    private readonly record struct Entry(TransactionFootprint Footprint, BlockHeader Header, IReleaseSpec Spec);
}
