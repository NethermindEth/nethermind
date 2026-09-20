// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

public class ReconstructionBroadcastTests
{
    private const ulong Slot = 42;
    private const ulong ProposerIndex = 7;

    private static SignedBeaconBlockHeader Header() => new()
    {
        Message = new BeaconBlockHeader
        {
            Slot = Slot,
            ProposerIndex = ProposerIndex,
            ParentRoot = Hash256.Zero,
            StateRoot = Hash256.Zero,
            BodyRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static DataColumnSidecar[] FullMatrix() =>
        [.. Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => new DataColumnSidecar { Index = (ulong)c, SignedBlockHeader = Header() })];

    [Test]
    public void Selects_only_columns_absent_from_the_held_set()
    {
        DataColumnSidecar[] fullMatrix = FullMatrix();
        DataColumnSidecar[] held = [fullMatrix[0], fullMatrix[3], fullMatrix[126]];

        IReadOnlyList<ReconstructedSidecarToPublish> newlyReconstructed = ReconstructionBroadcast.SelectNewlyReconstructed(held, fullMatrix);

        Assert.Multiple(() =>
        {
            Assert.That(newlyReconstructed, Has.Count.EqualTo(Eip7594DasConstants.NumberOfColumns - 3));
            Assert.That(newlyReconstructed.Select(e => e.Sidecar.Index), Does.Not.Contain(0ul));
            Assert.That(newlyReconstructed.Select(e => e.Sidecar.Index), Does.Not.Contain(3ul));
            Assert.That(newlyReconstructed.Select(e => e.Sidecar.Index), Does.Not.Contain(126ul));
            Assert.That(newlyReconstructed.Select(e => e.Sidecar.Index), Does.Contain(1ul));
        });
    }

    [Test]
    public void Carries_the_anti_equivocation_key_and_subnet_for_every_selected_entry()
    {
        DataColumnSidecar[] fullMatrix = FullMatrix();

        IReadOnlyList<ReconstructedSidecarToPublish> newlyReconstructed = ReconstructionBroadcast.SelectNewlyReconstructed([], fullMatrix);

        Assert.That(newlyReconstructed, Has.Count.EqualTo(Eip7594DasConstants.NumberOfColumns));
        Assert.Multiple(() =>
        {
            foreach (ReconstructedSidecarToPublish entry in newlyReconstructed)
            {
                Assert.That(entry.Slot, Is.EqualTo(Slot));
                Assert.That(entry.ProposerIndex, Is.EqualTo(ProposerIndex));
                Assert.That(entry.Subnet, Is.EqualTo(CustodyGroups.ComputeSubnetForDataColumnSidecar(entry.Sidecar.Index)));
            }
        });
    }

    [Test]
    public void Fully_held_matrix_selects_nothing_to_publish()
    {
        DataColumnSidecar[] fullMatrix = FullMatrix();

        IReadOnlyList<ReconstructedSidecarToPublish> newlyReconstructed = ReconstructionBroadcast.SelectNewlyReconstructed(fullMatrix, fullMatrix);

        Assert.That(newlyReconstructed, Is.Empty);
    }

    [Test]
    public void Empty_full_matrix_selects_nothing() =>
        Assert.That(ReconstructionBroadcast.SelectNewlyReconstructed([], []), Is.Empty);

    [Test]
    public void A_full_matrix_with_no_block_header_on_its_first_entry_is_refused_rather_than_published_under_a_guessed_key()
    {
        DataColumnSidecar[] fullMatrix = FullMatrix();
        fullMatrix[0].SignedBlockHeader = null;

        IReadOnlyList<ReconstructedSidecarToPublish> newlyReconstructed = ReconstructionBroadcast.SelectNewlyReconstructed([], fullMatrix);

        Assert.That(newlyReconstructed, Is.Empty);
    }

    [Test]
    public void Held_entries_with_an_out_of_range_index_are_ignored_rather_than_indexed_directly()
    {
        DataColumnSidecar[] fullMatrix = FullMatrix();
        DataColumnSidecar[] held = [new DataColumnSidecar { Index = (ulong)Eip7594DasConstants.NumberOfColumns + 1, SignedBlockHeader = Header() }];

        Assert.DoesNotThrow(() => ReconstructionBroadcast.SelectNewlyReconstructed(held, fullMatrix));
    }
}
