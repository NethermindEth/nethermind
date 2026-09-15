// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Seeds a trace's world state from the changeset index: the state before transaction T of a covered block
/// is the state at the parent with the writes of transactions 0..T-1 laid over it. Laying them over needs the
/// parent's version of every account the prefix touched, one cold history seek each; those are read in parallel
/// first, so the seeder's own reads land in memory.</summary>
public sealed class ChangesetPrefixStateSeedSource(
    TransactionChangesetIndex index,
    IStateReader? parentState = null,
    Func<Block, BlockHeader?>? parentHeader = null) : IPrefixStateSeedSource
{
    internal static readonly int WarmParallelism = Math.Clamp(Environment.ProcessorCount * 4, 8, 64);

    public bool TrySeed(Block block, int transactionIndex, IWorldState state, IReleaseSpec spec)
    {
        if (transactionIndex <= 0 || transactionIndex > ChangesetKeyLayout.MaxTransactionIndex || block.Hash is null) return false;
        if (!index.TryRentOverlay((ulong)block.Number, block.Hash, (ushort)transactionIndex, out MidBlockOverlayCache.Lease lease)) return false;

        using (lease)
        {
            if (parentState is not null && parentHeader?.Invoke(block) is { } parent) Warm(parent, lease.Overlay);
            return PrefixStateSeeder.TryApply(lease.Overlay, state, spec);
        }
    }

    private void Warm(BlockHeader parent, MidBlockOverlay overlay)
    {
        List<Address> addresses = [];
        List<StorageCell> cells = [];
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = overlay.Accounts;
        while (accounts.MoveNext())
        {
            if (!Gone(accounts.Current.Value)) addresses.Add(accounts.Current.Key);
        }

        Dictionary<StorageCell, MidBlockOverlay.StorageWrite>.Enumerator writes = overlay.Writes;
        while (writes.MoveNext())
        {
            StorageCell cell = writes.Current.Key;
            if (!(overlay.TryGetAccount(cell.Address, out MidBlockOverlay.AccountOverlay? account) && Gone(account))) cells.Add(cell);
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = WarmParallelism };
        try
        {
            Parallel.ForEach(addresses, options, address => parentState!.TryGetAccount(parent, address, out _));
            Parallel.ForEach(cells, options, cell => parentState!.GetStorage(parent, cell.Address, cell.Index, out _));
        }
        catch (AggregateException)
        {
        }
    }

    private static bool Gone(MidBlockOverlay.AccountOverlay account) => account.Emptied && !account.Exists;
}
