// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Locks the consensus contract of the verify-only storage-read validation lanes - the structural
/// "every declared read was executed" equivalence and the per-slice chargeable read budget - by
/// driving synthetic per-tx slices through the REAL generated validation index
/// (<c>RegisterGeneratedSlice</c>), not the <c>GeneratedBlockAccessList.Merge</c> shortcut that
/// bypasses it. These outcomes must hold identically once read materialization is replaced by the
/// dense-ordinal coverage bitmap.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class BlockAccessListReadCoverageValidationTests
{
    private static readonly Address SystemContract = Eip7002Constants.WithdrawalRequestPredeployAddress;

    private static IEnumerable<TestCaseData> ReadCoverageCases()
    {
        // ---- valid: generated reads exactly cover the suggested declared reads ----
        yield return Case("declared read covered once",
            Suggested(Reads(TestItem.AddressA, 5)),
            shouldThrow: false,
            Slice(s => s.AddStorageRead(TestItem.AddressA, 5)));

        // Same slot read twice in one slice collapses to a single structural read and is charged once.
        yield return Case("repeat read in one slice counts once",
            Suggested(Reads(TestItem.AddressA, 5)),
            shouldThrow: false,
            Slice(s => { s.AddStorageRead(TestItem.AddressA, 5); s.AddStorageRead(TestItem.AddressA, 5); }));

        // Same slot read in two slices: structurally one read (matches suggested), but the chargeable
        // budget counts it per slice (2). With suggestedChargeable - generatedChargeable < 0 the budget
        // never trips, and the structural set still matches - so this is valid.
        yield return Case("repeat read across two slices preserves per-slice budget",
            Suggested(Reads(TestItem.AddressA, 5)),
            shouldThrow: false,
            Slice(s => s.AddStorageRead(TestItem.AddressA, 5)),
            Slice(s => s.AddStorageRead(TestItem.AddressA, 5)));

        // ---- invalid: structural content mismatch (same count, different slot) ----
        yield return Case("declared read value mismatch",
            Suggested(Reads(TestItem.AddressA, 5)),
            shouldThrow: true,
            Slice(s => s.AddStorageRead(TestItem.AddressA, 7)));

        // ---- invalid: suggested declares a read on an account execution never touched ----
        yield return Case("suggested read on untouched account",
            Suggested(Reads(TestItem.AddressA, 5)),
            shouldThrow: true,
            Slice(s => s.AddStorageRead(TestItem.AddressB, 5)));

        // ---- invalid: suggested declares more reads than were executed (count short) ----
        yield return Case("suggested declares more reads than executed",
            Suggested(Reads(TestItem.AddressA, 5, 6)),
            shouldThrow: true,
            Slice(s => s.AddStorageRead(TestItem.AddressA, 5)));

        // ---- system-contract reads are structurally compared but excluded from the gas budget ----
        // Valid: system + non-system reads both match; only AddressA is chargeable on each side.
        yield return Case("system-contract reads structurally compared (match)",
            Suggested(Reads(SystemContract, 0, 1), Reads(TestItem.AddressA, 5)),
            shouldThrow: false,
            Slice(s =>
            {
                s.AddStorageRead(SystemContract, 0);
                s.AddStorageRead(SystemContract, 1);
                s.AddStorageRead(TestItem.AddressA, 5);
            }));

        // Invalid via the structural lane only: the system read differs, and since system reads carry
        // no chargeable weight the budget check cannot catch it - only the structural compare does.
        yield return Case("system-contract read mismatch caught structurally not by budget",
            Suggested(Reads(SystemContract, 0)),
            shouldThrow: true,
            Slice(s => s.AddStorageRead(SystemContract, 1)));
    }

    [TestCaseSource(nameof(ReadCoverageCases))]
    public void VerifyOnly_storage_read_validation(
        ReadOnlyBlockAccessList suggested,
        bool shouldThrow,
        Action<BlockAccessListAtIndex>[] generatedSlices)
    {
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        using BlockAccessListManager balManager = CreateBalManager(stateProvider);

        Block block = Build.A.Block
            .WithNumber(1)
            .WithGasUsed(0) // gasRemaining = 0, so any surplus declared read trips the budget check
            .WithBlockAccessList(suggested)
            .TestObject;

        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);

        uint index = 0;
        foreach (Action<BlockAccessListAtIndex> populate in generatedSlices)
        {
            BlockAccessListAtIndex slice = new() { Index = index++ };
            populate(slice);
            balManager.RegisterGeneratedSliceForTest(slice);
        }

        if (shouldThrow)
        {
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => balManager.SetBlockAccessList(block));
        }
        else
        {
            Assert.DoesNotThrow(() => balManager.SetBlockAccessList(block));
        }
    }

    // Coverage path (PreBlockCaches present -> verify-only marks per-worker coverage instead of
    // materializing reads). A system-contract read is the uncovered case so it is caught by coverage
    // alone, not masked by the chargeable budget (system reads carry no chargeable weight).
    [Test]
    public void Coverage_AllDeclaredReadsCovered_DoesNotThrow()
        => RunCoverageValidation(allReadsCovered: true);

    [Test]
    public void Coverage_UncoveredSystemRead_ThrowsViaCoverageNotBudget()
        => RunCoverageValidation(allReadsCovered: false);

    private static void RunCoverageValidation(bool allReadsCovered)
    {
        Address system = Eip7002Constants.WithdrawalRequestPredeployAddress;
        ReadOnlyBlockAccessList suggested = Suggested(Reads(system, 0), Reads(TestItem.AddressA, 5));

        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);
        PreBlockCaches caches = new();
        using BlockAccessListManager balManager = CreateBalManager(stateProvider, caches);

        Block block = Build.A.Block.WithNumber(1).WithGasUsed(0).WithBlockAccessList(suggested).TestObject;
        balManager.PrepareForProcessing(block, Amsterdam.Instance, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new(block.Header, Amsterdam.Instance));
        balManager.Setup(block);
        Assert.That(caches.ReadCoverageEnabled, Is.True, "verify-only parallel must enable coverage");

        // A slice marking both accounts as accessed (account reads), so account-set equivalence holds;
        // its chargeable count is the non-system read (AddressA) only.
        BlockAccessListAtIndex slice = new() { Index = 0, ChargeableReadCount = 1 };
        slice.AddAccountRead(system);
        slice.AddAccountRead(TestItem.AddressA);
        balManager.RegisterGeneratedSliceForTest(slice);

        // Simulate execution covering the reads: always AddressA:5 (chargeable); the system read only
        // when allReadsCovered, otherwise it is the uncovered gap.
        BalReadCoverage coverage = caches.RentReadCoverage();
        MarkCoverage(caches, coverage, TestItem.AddressA, 5);
        if (allReadsCovered) MarkCoverage(caches, coverage, system, 0);

        if (allReadsCovered)
            Assert.DoesNotThrow(() => balManager.SetBlockAccessList(block));
        else
            Assert.Throws<BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException>(
                () => balManager.SetBlockAccessList(block));
    }

    private static void MarkCoverage(PreBlockCaches caches, BalReadCoverage coverage, Address address, UInt256 slot)
    {
        Assert.That(caches.StorageReadPlan!.TryGetGlobalReadOrdinal(address, slot, out int ordinal), Is.True);
        coverage.MarkRead(ordinal, chargeable: address != Eip7002Constants.WithdrawalRequestPredeployAddress);
    }

    private static TestCaseData Case(
        string name,
        ReadOnlyBlockAccessList suggested,
        bool shouldThrow,
        params Action<BlockAccessListAtIndex>[] generatedSlices)
        => new TestCaseData(suggested, shouldThrow, generatedSlices).SetName(name);

    private static Action<BlockAccessListAtIndex> Slice(Action<BlockAccessListAtIndex> populate) => populate;

    private static ReadOnlyAccountChanges Reads(Address address, params UInt256[] slots)
        => Build.An.AccountChanges.WithAddress(address).WithStorageReads(slots).TestObject;

    private static ReadOnlyBlockAccessList Suggested(params ReadOnlyAccountChanges[] accounts)
        => Build.A.BlockAccessList.WithAccountChanges(accounts).TestObject;

    private static BlockAccessListManager CreateBalManager(IWorldState stateProvider, PreBlockCaches? preBlockCaches = null) =>
        new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            preBlockCaches: preBlockCaches);
}
