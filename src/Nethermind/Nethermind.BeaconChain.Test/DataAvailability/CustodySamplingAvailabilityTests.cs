// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.SszRest;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.DataAvailability;

/// <summary>
/// The production <c>is_data_available</c> on its own: which columns it asks for, and what it takes
/// for a held column to count. Uses the real KZG sidecars of <see cref="ImportableBlobBlock"/>, so
/// "verified" here means the real inclusion proof and cell proofs, not a stub.
/// </summary>
public class CustodySamplingAvailabilityTests
{
    private static readonly Hash256 NodeId = new([.. Enumerable.Repeat((byte)0x37, 32)]);

    private static NodeColumnCustody BaseCustody() => new(NodeId, Eip7594DasConstants.CustodyRequirement);

    [Test]
    public void A_block_without_blob_commitments_is_available_without_identity_or_columns()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.CreateWithoutBlobs();
        RecordingColumnSource columns = new(chain, []);
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), columns, chain.ClockAtEpoch(0));

        bool available = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(columns.Requested, Is.Empty, "nothing to retrieve for a block that committed to no blobs");
        });
    }

    [Test]
    public void Fails_closed_while_the_node_identity_is_unknown()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), new RecordingColumnSource(chain, All()), chain.ClockAtEpoch(0));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
    }

    [Test]
    public void Available_once_every_custody_and_sampled_column_is_held_and_asks_for_exactly_those()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        RecordingColumnSource columns = new(chain, custody.SampledColumns);
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), columns, chain.ClockAtEpoch(0));

        bool available = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True);
            Assert.That(columns.Requested.Select(r => r.Column).Distinct(), Is.EquivalentTo(custody.SampledColumns),
                "the rule demands this node's own sample (which contains its custody), never the whole matrix");
            Assert.That(columns.Requested.Select(r => r.BlockRoot), Is.All.EqualTo(chain.BlockRoot), "columns are looked up for this block, not by index alone");
            Assert.That(columns.Requested, Has.Count.EqualTo(custody.SampledColumns.Count), "each column is retrieved and verified once, although it may sit in both sets");
        });
    }

    [Test]
    public void Unavailable_when_one_custody_column_is_missing()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns.Where(c => c != custody.CustodyColumns[^1])), chain.ClockAtEpoch(0));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
    }

    [Test]
    public void Unavailable_when_one_sampled_only_column_is_missing()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        ulong sampledOnly = custody.SampledColumns.First(c => !custody.CustodyColumns.Contains(c));
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns.Where(c => c != sampledOnly)), chain.ClockAtEpoch(0));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False, "das-core: sampling succeeds only if every selected column is retrieved");
    }

    [Test]
    public void A_held_column_with_a_tampered_cell_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecar tampered = chain.Columns[(int)custody.SampledColumns[0]];
        byte[] cell = tampered.Column![0].AsSpan().ToArray();
        cell[^1] ^= 0x01;
        tampered.Column[0] = SszBlobCell.FromSpan(cell);
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns), chain.ClockAtEpoch(0));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False, "the source only says it holds a column; the rule must still prove it");
    }

    [Test]
    public void A_held_column_claiming_different_commitments_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        // The block's two commitments in the other order: the held column's inclusion and cell proofs
        // still verify against its own header, so only the per-index cross-check can reject it.
        SszKzgCommitment[] blockCommitments = chain.Block.Message!.Body!.BlobKzgCommitments!;
        chain.Block.Message.Body.BlobKzgCommitments = [blockCommitments[1], blockCommitments[0]];
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns), chain.ClockAtEpoch(0));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
    }

    [Test]
    public void Below_the_availability_window_a_blob_block_is_available_without_identity_or_columns()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        RecordingColumnSource columns = new(chain, []);
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), columns, chain.ClockAtEpoch(Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests + 1));

        bool available = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);

        Assert.Multiple(() =>
        {
            Assert.That(available, Is.True, "the network no longer guarantees to serve this epoch's columns, so none can be demanded");
            Assert.That(columns.Requested, Is.Empty, "the window is decided before any column is looked up");
        });
    }

    /// <summary>
    /// The window is a wall-clock window, re-read at every check: one rule instance must change its
    /// verdict on the same block as the clock advances, and epoch 0 stays inside the window until
    /// the clock is strictly more than <c>MIN_EPOCHS_FOR_DATA_COLUMN_SIDECARS_REQUESTS</c> epochs past it.
    /// </summary>
    [Test]
    public void The_window_follows_the_wall_clock_at_every_check()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)chain.Spec.GenesisTime).UtcDateTime);
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), new RecordingColumnSource(chain, []), new SlotClock(chain.Spec, timestamper));
        TimeSpan epoch = TimeSpan.FromSeconds(chain.Spec.SlotsPerEpoch * chain.Spec.SecondsPerSlot);

        bool atGenesis = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);
        timestamper.Add(epoch * Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests);
        bool atTheWindowEdge = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);
        timestamper.Add(epoch);
        bool oneEpochPast = rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec);

        Assert.Multiple(() =>
        {
            Assert.That(atGenesis, Is.False, "inside the window, with no identity, the rule fails closed");
            Assert.That(atTheWindowEdge, Is.False, "epoch 0 is the window's first epoch when the clock reads exactly the window width");
            Assert.That(oneEpochPast, Is.True, "one epoch later the same instance sees the block leave the window");
        });
    }

    private static IEnumerable<ulong> All() => Enumerable.Range(0, Eip7594DasConstants.NumberOfColumns).Select(c => (ulong)c);

    private sealed class FixedCustodySource(NodeColumnCustody? custody) : INodeColumnCustodySource
    {
        public NodeColumnCustody? Current => custody;
    }

    /// <summary>Holds the given columns of the fixture block and records every lookup made against it.</summary>
    private sealed class RecordingColumnSource(ImportableBlobBlock chain, IEnumerable<ulong> held) : IDataColumnSource
    {
        private readonly HashSet<ulong> _held = [.. held];

        public List<(Hash256 BlockRoot, ulong Column)> Requested { get; } = [];

        public bool TryGetColumn(Hash256 blockRoot, ulong columnIndex, [NotNullWhen(true)] out DataColumnSidecar? sidecar)
        {
            Requested.Add((blockRoot, columnIndex));
            sidecar = blockRoot == chain.BlockRoot && _held.Contains(columnIndex) ? chain.Columns[(int)columnIndex] : null;
            return sidecar is not null;
        }
    }
}
