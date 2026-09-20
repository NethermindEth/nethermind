// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
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
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), columns);

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
        CustodySamplingAvailability rule = new(new FixedCustodySource(null), new RecordingColumnSource(chain, All()));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
    }

    [Test]
    public void Available_once_every_custody_and_sampled_column_is_held_and_asks_for_exactly_those()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        RecordingColumnSource columns = new(chain, custody.SampledColumns);
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), columns);

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
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns.Where(c => c != custody.CustodyColumns[^1])));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
    }

    [Test]
    public void Unavailable_when_one_sampled_only_column_is_missing()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        ulong sampledOnly = custody.SampledColumns.First(c => !custody.CustodyColumns.Contains(c));
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns.Where(c => c != sampledOnly)));

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
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False, "the source only says it holds a column; the rule must still prove it");
    }

    [Test]
    public void A_held_column_claiming_different_commitments_does_not_count()
    {
        ImportableBlobBlock chain = ImportableBlobBlock.Create();
        NodeColumnCustody custody = BaseCustody();
        DataColumnSidecar foreign = chain.Columns[(int)custody.SampledColumns[0]];
        foreign.KzgCommitments = [foreign.KzgCommitments![0]]; // one of the block's two commitments
        CustodySamplingAvailability rule = new(new FixedCustodySource(custody), new RecordingColumnSource(chain, custody.SampledColumns));

        Assert.That(rule.IsDataAvailable(chain.Block.Message!, chain.BlockRoot, chain.Spec), Is.False);
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
