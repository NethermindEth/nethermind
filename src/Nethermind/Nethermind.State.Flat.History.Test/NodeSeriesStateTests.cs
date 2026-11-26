// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.History.Walk;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class NodeSeriesStateTests
{
    private const ulong Blocks = 16 * CommitmentDepthPolicy.FullVectorEvery;
    private const ulong Anchor = Blocks - 1;
    private static readonly TreePath Path = TreePath.FromNibble([1, 2]);
    private static readonly SeriesKey Key = SeriesScope.Accounts.Key(Path, scratch: true);
    private static readonly ValueHash256 Quiet = ValueKeccak.Compute([0xAA]);

    private SnapshotableMemColumnsDb<FlatHistoryColumns> _history = null!;

    [SetUp]
    public void SetUp() => _history = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _history.Dispose();

    [Test]
    public void MaterializeStart_AtAChunkAnchorWithAQuietAndABusyChild_ReadsAtMostOneFullVectorInterval()
    {
        using (SeriesWriter writer = new(_history))
        using (SeriesPublisher publisher = new(SeriesScope.Accounts, Path, Key, writer))
        {
            for (ulong block = 0; block < Blocks; block++)
            {
                NodeView view = QuietAndBusy(block);
                publisher.Publish(block, view, emitter: null);
                view.Release();
            }
        }

        ResolutionBudget rowsRead = new(long.MaxValue);
        using NodeSeriesState state = Materialize(rowsRead, CancellationToken.None);
        AssertReconstructsTheAnchor(state);
        Assert.That(rowsRead.ScannedRows, Is.LessThanOrEqualTo(CommitmentDepthPolicy.FullVectorEvery),
            "a scratch series writes a full vector every FullVectorEvery branch rows, so the anchor of a chunk is rebuilt without walking back to the quiet child's only change at block 0");
    }

    [Test]
    public void MaterializeStart_OnADeltaOnlySeriesFromAnOlderBinary_ReconstructsTheAnchor()
    {
        WriteDeltaOnlySeries();

        ResolutionBudget rowsRead = new(long.MaxValue);
        using NodeSeriesState state = Materialize(rowsRead, CancellationToken.None);
        AssertReconstructsTheAnchor(state);
        Assert.That(rowsRead.ScannedRows, Is.EqualTo((long)Blocks), "scratch from a walk interrupted before the upgrade has no full vectors and is read back to block 0");
    }

    [Test]
    public void MaterializeStart_WhenCancelledOnALongDeltaOnlySeries_Throws()
    {
        WriteDeltaOnlySeries();

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.That(() => Materialize(budget: null, cancelled.Token).Dispose(), Throws.InstanceOf<OperationCanceledException>());
    }

    private void WriteDeltaOnlySeries()
    {
        using SeriesWriter writer = new(_history);
        for (ulong block = 0; block < Blocks; block++)
        {
            NodeView view = QuietAndBusy(block);
            ChildVector children = view.Children!;
            writer.WriteBranch(Key, block, children.Presence, block == 0 ? children.Presence : (ushort)0b10, children);
            view.Release();
        }
    }

    private NodeSeriesState Materialize(ResolutionBudget? budget, CancellationToken token)
    {
        Span<byte> prefix = stackalloc byte[SeriesKey.MaxKeyLength];
        int prefixLength = Key.WritePrefix(prefix);
        ISortedKeyValueStore column = (ISortedKeyValueStore)_history.GetColumnDb(FlatHistoryColumns.AccountCommitments);
        using CommitmentStore.RowChain chain = new(column, prefix[..prefixLength], CommitmentKeyLayout.FineTier, Anchor, budget, epoch: null, minEpoch: 0);
        NodeSeriesState state = new();
        try
        {
            Assert.That(chain.MoveNext(), Is.True);
            state.MaterializeStart(chain, token);
            return state;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    private static void AssertReconstructsTheAnchor(NodeSeriesState state)
    {
        NodeView expected = QuietAndBusy(Anchor);
        NodeView actual = state.ToView();
        try
        {
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
        }
        finally
        {
            expected.Release();
            actual.Release();
        }
    }

    private static NodeView QuietAndBusy(ulong block)
    {
        ChildVector children = ChildVector.Rent();
        children.SetHash(0, Quiet);
        children.SetHash(1, ValueKeccak.Compute(BitConverter.GetBytes(block)));
        return NodeView.Branch(children);
    }
}
