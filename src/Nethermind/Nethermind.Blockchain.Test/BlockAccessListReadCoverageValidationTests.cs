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
        using BlockAccessListManager balManager = CreateAmsterdamBalManager(stateProvider);

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

    private static BlockAccessListManager CreateAmsterdamBalManager(IWorldState stateProvider) =>
        new(
            stateProvider,
            new TestSingleReleaseSpecProvider(Amsterdam.Instance),
            Substitute.For<IBlockhashProvider>(),
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = true },
            new WithdrawalProcessorFactory(LimboLogs.Instance));
}
