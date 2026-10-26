// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// One <see cref="DataColumnSidecar"/> that <see cref="DataColumnReconstruction.TryReconstruct"/>
/// produced but this node did not itself receive, with the pieces the P2P layer needs to treat it
/// exactly as if it had arrived over gossip (das-core.md, "Reconstruction and cross-seeding"): the
/// anti-equivocation cache key, and which subnet to publish it to.
/// </summary>
/// <param name="Slot">signed_block_header.message.slot: part of the anti-equivocation tuple.</param>
/// <param name="ProposerIndex">signed_block_header.message.proposer_index: part of the anti-equivocation tuple.</param>
/// <param name="Subnet"><c>compute_subnet_for_data_column_sidecar(sidecar.index)</c>.</param>
/// <param name="Sidecar">The reconstructed sidecar itself.</param>
public readonly record struct ReconstructedSidecarToPublish(ulong Slot, ulong ProposerIndex, ulong Subnet, DataColumnSidecar Sidecar);

/// <summary>
/// Fulu das-core.md, "Reconstruction and cross-seeding": reconstructed columns are exposed as if
/// received over the network. The P2P layer updates its anti-equivocation cache and publishes through
/// mesh peers on subscribed subnets or fanout peers on unsubscribed subnets.
/// </summary>
public static class ReconstructionBroadcast
{
    /// <summary>
    /// From a successful <see cref="DataColumnReconstruction.TryReconstruct"/>, the entries of
    /// <paramref name="fullMatrix"/> not already present (by column index) in
    /// <paramref name="heldColumns"/>. Columns already held were already marked seen - and published,
    /// if received over gossip - when they first arrived; re-emitting them here would only be
    /// rejected as a duplicate by the very cache this exists to update, so they are filtered out here
    /// rather than left for the P2P layer to notice.
    /// </summary>
    /// <returns>
    /// Empty if <paramref name="fullMatrix"/> is empty or its first entry carries no block header:
    /// with no header there is no anti-equivocation key to publish under, and guessing one would
    /// corrupt the cache rather than merely skip a publish. Also empty if any entry of
    /// <paramref name="heldColumns"/> carries a different block's header: pairing columns by index
    /// alone, with no check that both arguments came from the same <c>TryReconstruct</c> call, is
    /// exactly the defect <see cref="DataColumnReconstruction"/> was already fixed for once (two
    /// blocks sharing a blob count recovering into a matrix belonging to neither); trusting the
    /// caller to have paired them correctly here would reopen it one layer up.
    /// </returns>
    public static IReadOnlyList<ReconstructedSidecarToPublish> SelectNewlyReconstructed(
        IReadOnlyList<DataColumnSidecar> heldColumns, DataColumnSidecar[] fullMatrix)
    {
        if (fullMatrix.Length == 0 || fullMatrix[0].SignedBlockHeader?.Message is not { } header)
        {
            return [];
        }

        bool[] alreadyHeld = new bool[Eip7594DasConstants.NumberOfColumns];
        foreach (DataColumnSidecar held in heldColumns)
        {
            if (held.SignedBlockHeader?.Message?.BodyRoot != header.BodyRoot)
            {
                return [];
            }

            if (held.Index < (ulong)Eip7594DasConstants.NumberOfColumns)
            {
                alreadyHeld[(int)held.Index] = true;
            }
        }

        List<ReconstructedSidecarToPublish> newlyReconstructed = [];
        foreach (DataColumnSidecar sidecar in fullMatrix)
        {
            if (sidecar.Index >= (ulong)Eip7594DasConstants.NumberOfColumns || alreadyHeld[(int)sidecar.Index])
            {
                continue;
            }

            ulong subnet = CustodyGroups.ComputeSubnetForDataColumnSidecar(sidecar.Index);
            newlyReconstructed.Add(new ReconstructedSidecarToPublish(header.Slot, header.ProposerIndex, subnet, sidecar));
        }

        return newlyReconstructed;
    }
}
