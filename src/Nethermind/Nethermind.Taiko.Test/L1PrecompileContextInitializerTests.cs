// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Taiko.Precompiles;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

[TestFixture]
public class L1PrecompileContextInitializerTests
{
    private const long L2BlockNumber = 42;
    private static readonly UInt256 AnchorBlockId = 1_000;
    private static readonly long L1BlockHeight = 1_500;

    [TearDown]
    public void TearDown() => L1PrecompileExecutionContext.Clear();

    [Test]
    public void TrySet_ValidV4_SetsContext()
    {
        Transaction tx = MakeAnchorTx(TaikoBlockValidator.AnchorV4Selector, AnchorBlockId, extraBytes: 0);
        MockL1OriginStore store = MockL1OriginStore.WithHeight(L1BlockHeight);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.True);
        (UInt256 Anchor, UInt256 L1Origin)? ctx = L1PrecompileExecutionContext.Get();
        Assert.That(ctx, Is.Not.Null);
        Assert.That(ctx!.Value.Anchor, Is.EqualTo(AnchorBlockId));
        Assert.That(ctx.Value.L1Origin, Is.EqualTo((UInt256)L1BlockHeight));
    }

    [Test]
    public void TrySet_ValidV4WithSignalSlots_SetsContext()
    {
        Transaction tx = MakeAnchorTx(TaikoBlockValidator.AnchorV4WithSignalSlotsSelector, AnchorBlockId, extraBytes: 64);
        MockL1OriginStore store = MockL1OriginStore.WithHeight(L1BlockHeight);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.True);
        (UInt256 Anchor, UInt256 L1Origin)? ctx = L1PrecompileExecutionContext.Get();
        Assert.That(ctx!.Value.Anchor, Is.EqualTo(AnchorBlockId));
    }

    [Test]
    public void TrySet_WrongSelector_LeavesContextUnset()
    {
        byte[] bogusSelector = [0xDE, 0xAD, 0xBE, 0xEF];
        Transaction tx = MakeAnchorTx(bogusSelector, AnchorBlockId, extraBytes: 0);
        MockL1OriginStore store = MockL1OriginStore.WithHeight(L1BlockHeight);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.False);
        Assert.That(L1PrecompileExecutionContext.Get(), Is.Null);
    }

    [Test]
    public void TrySet_TooShortData_LeavesContextUnset()
    {
        Transaction tx = new() { Data = new byte[36] }; // below AnchorV4MinimumLength=68
        MockL1OriginStore store = MockL1OriginStore.WithHeight(L1BlockHeight);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.False);
        Assert.That(L1PrecompileExecutionContext.Get(), Is.Null);
    }

    [Test]
    public void TrySet_NonAnchorIndex_LeavesContextUnset()
    {
        Transaction tx = MakeAnchorTx(TaikoBlockValidator.AnchorV4Selector, AnchorBlockId, extraBytes: 0);
        MockL1OriginStore store = MockL1OriginStore.WithHeight(L1BlockHeight);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(1, tx, L2BlockNumber, store);

        Assert.That(ok, Is.False);
        Assert.That(L1PrecompileExecutionContext.Get(), Is.Null);
    }

    [Test]
    public void TrySet_ReadL1Origin_ReturnsNull_LeavesContextUnset()
    {
        Transaction tx = MakeAnchorTx(TaikoBlockValidator.AnchorV4Selector, AnchorBlockId, extraBytes: 0);
        MockL1OriginStore store = MockL1OriginStore.ReturningNull();

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.False);
        Assert.That(L1PrecompileExecutionContext.Get(), Is.Null);
    }

    [TestCase((long)0, Description = "Preconf block — height == 0")]
    [TestCase((long)-1, Description = "Negative height — defensive")]
    public void TrySet_NonPositiveL1BlockHeight_LeavesContextUnset(long height)
    {
        Transaction tx = MakeAnchorTx(TaikoBlockValidator.AnchorV4Selector, AnchorBlockId, extraBytes: 0);
        MockL1OriginStore store = MockL1OriginStore.WithHeight(height);

        bool ok = L1PrecompileContextInitializer.TrySetFromAnchorTransaction(0, tx, L2BlockNumber, store);

        Assert.That(ok, Is.False);
        Assert.That(L1PrecompileExecutionContext.Get(), Is.Null);
    }

    [Test]
    public void ValidateBlockRange_UnsetContext_AcceptsAnyBlock()
    {
        L1PrecompileExecutionContext.Clear();
        (bool isValid, string? reason) = L1PrecompileExecutionContext.ValidateBlockRange(blockNumber: (UInt256)ulong.MaxValue);
        Assert.That(isValid, Is.True);
        Assert.That(reason, Is.Null);
    }

    [Test]
    public void ValidateBlockRange_AtUpperInclusiveEdge_Accepted()
    {
        L1PrecompileExecutionContext.Set(anchor: 500, l1Origin: 1000);
        (bool isValid, _) = L1PrecompileExecutionContext.ValidateBlockRange(blockNumber: (UInt256)1000);
        Assert.That(isValid, Is.True);
    }

    /// <summary>
    /// Builds a synthetic anchor-v4 transaction: 4-byte selector + 32-byte anchorBlockId
    /// + <paramref name="extraBytes"/> trailing bytes (for v4-with-signal-slots padding).
    /// </summary>
    private static Transaction MakeAnchorTx(byte[] selector, UInt256 anchorBlockId, int extraBytes)
    {
        byte[] data = new byte[68 + extraBytes];
        selector.CopyTo(data.AsSpan(0, 4));
        anchorBlockId.ToBigEndian().CopyTo(data.AsSpan(4, 32));
        return new Transaction { Data = data };
    }

    private sealed class MockL1OriginStore : IL1OriginStore
    {
        private readonly L1Origin? _origin;

        private MockL1OriginStore(L1Origin? origin) => _origin = origin;

        public static MockL1OriginStore WithHeight(long height) =>
            new(new L1Origin(
                blockId: (UInt256)L2BlockNumber,
                l2BlockHash: null,
                l1BlockHeight: height,
                l1BlockHash: default,
                buildPayloadArgsId: null));

        public static MockL1OriginStore ReturningNull() => new(null);

        public L1Origin? ReadL1Origin(UInt256 blockId) => _origin;
        public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin) => throw new NotSupportedException();
        public UInt256? ReadHeadL1Origin() => null;
        public void WriteHeadL1Origin(UInt256 blockId) => throw new NotSupportedException();
        public UInt256? ReadBatchToLastBlockID(UInt256 batchId) => null;
        public void WriteBatchToLastBlockID(UInt256 batchId, UInt256 blockId) => throw new NotSupportedException();
    }
}
