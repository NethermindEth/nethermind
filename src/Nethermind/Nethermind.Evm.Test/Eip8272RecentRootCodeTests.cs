// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Executes <see cref="Eip8272Constants.RecentRootCode"/> as ordinary EVM code and checks the commitment it
/// leaves in storage against the derivations the reference check reads.
/// </summary>
[TestFixture]
public class Eip8272RecentRootCodeTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;

    private const ulong SlotNumber = 12_345;

    private static readonly ValueHash256 Salt = TestItem.KeccakA.ValueHash256;
    private static readonly ValueHash256 Root = TestItem.KeccakB.ValueHash256;

    [Test]
    public void Commits_the_entry_a_reference_to_the_written_slot_reads()
    {
        TestAllTracerWithOutput result = Write(Bytes.Concat(Salt.Bytes, Root.Bytes));

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(Stored(SlotNumber), Is.EqualTo(ExpectedEntryHash()));
    }

    /// <remarks>
    /// The ring index is the slot modulo <see cref="Eip8272Constants.RecentRootLength"/>, while the entry commits
    /// to the unaliased slot — writing to one index and reading from another would silently validate stale roots.
    /// </remarks>
    [Test]
    public void Commits_under_the_ring_index_of_the_current_slot()
    {
        Write(Bytes.Concat(Salt.Bytes, Root.Bytes));

        Assert.That(
            RecentRootStore.StorageKey(SourceId, SlotNumber % Eip8272Constants.RecentRootLength),
            Is.Not.EqualTo(RecentRootStore.StorageKey(SourceId, SlotNumber)));
        Assert.That(StoredAt(RecentRootStore.StorageKey(SourceId, SlotNumber)), Is.EqualTo(UInt256.Zero));
    }

    [TestCase(63, 0ul, TestName = "calldata shorter than a (salt, root) pair")]
    [TestCase(65, 0ul, TestName = "calldata longer than a (salt, root) pair")]
    [TestCase(64, 1ul, TestName = "value transferred with the write")]
    public void Refuses_a_call_the_write_operation_does_not_define(int calldataLength, ulong value)
    {
        byte[] calldata = Bytes.Concat(Salt.Bytes, Root.Bytes);
        Array.Resize(ref calldata, calldataLength);

        TestAllTracerWithOutput result = Write(calldata, value);

        Assert.That(result.Error, Is.EqualTo(EvmExceptionType.Revert.ToString()).IgnoreCase,
            "the entry check is an explicit REVERT, so a halt for any other reason means the guard was not what stopped the write");
        Assert.That(Stored(SlotNumber), Is.EqualTo(UInt256.Zero));
    }

    /// <remarks>
    /// The body carries the derivation constants as literals, so nothing in the source shows that they still agree
    /// with the values <see cref="RecentRootStore"/> derives from. Pinning them here is what makes the 144 bytes
    /// auditable: a domain string or a ring length changed on one side alone fails this before it can diverge
    /// between the write and the reference check.
    /// </remarks>
    [Test]
    public void Embeds_the_derivation_constants_the_reference_check_uses()
    {
        ReadOnlySpan<byte> code = Eip8272Constants.RecentRootCode.Span;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(code.IndexOf(Eip8272Constants.RecentRootEntryDomain.Bytes), Is.GreaterThanOrEqualTo(0),
                "the entry-hash domain separator");
            Assert.That(code.IndexOf(Eip8272Constants.RecentRootStorageDomain.Bytes), Is.GreaterThanOrEqualTo(0),
                "the storage-key domain separator");
            Assert.That(code.IndexOf((ReadOnlySpan<byte>)[0x61, .. ((ushort)(Eip8272Constants.RecentRootLength - 1)).ToBigEndianByteArray()]), Is.GreaterThanOrEqualTo(0),
                "PUSH2 of the ring-index mask");
        }
    }

    private static ValueHash256 SourceId => RecentRootStore.SourceId(Sender, Salt);

    private static UInt256 ExpectedEntryHash() =>
        new(RecentRootStore.EntryHash(SourceId, SlotNumber, Root).Bytes, isBigEndian: true);

    private UInt256 Stored(ulong slot) => StoredAt(RecentRootStore.StorageKey(SourceId, slot % Eip8272Constants.RecentRootLength));

    private UInt256 StoredAt(in ValueHash256 storageKey) =>
        new(TestState.Get(new StorageCell(Recipient, storageKey.ToUInt256())), isBigEndian: true);

    private TestAllTracerWithOutput Write(byte[] calldata, ulong value = 0)
    {
        Transaction transaction = Build.A.Transaction
            .WithGasLimit(200_000)
            .WithGasPrice(1UL)
            .WithData(calldata)
            .WithValue(value)
            .To(Recipient)
            .SignedAndResolved(SenderKey)
            .TestObject;

        (Block block, Transaction prepared) = PrepareTx(
            Activation,
            200_000,
            Eip8272Constants.RecentRootCode.ToArray(),
            slotNumber: SlotNumber,
            transaction: transaction);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(prepared, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer;
    }
}
