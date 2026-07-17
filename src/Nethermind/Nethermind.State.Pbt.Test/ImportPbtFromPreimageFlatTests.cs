// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

public class ImportPbtFromPreimageFlatTests
{
    private const ulong SourceBlock = 7;

    [Test]
    public async Task Imports_preimage_flat_state_into_pbt_and_exits()
    {
        // > 128 chunks (3968 bytes) so the overflow-code zone is exercised end-to-end
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);      // header-region slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()), pbtTarget, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, PbtReferenceModel.Root(model))));
        Assert.That(reader.GetAccount(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(reader.GetAccount(TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(EvmWordSlot.AsReadOnlySpan(reader.GetSlot(TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
    }

    [Test]
    public async Task Skips_when_pbt_already_populated()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        // seed the target so its persisted state is no longer pre-genesis
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        ValueHash256 existingRoot = new(Keccak.Compute("existing").Bytes);
        using (pbtTarget.CreateWriteBatch(StateId.PreGenesis, new StateId(1, existingRoot))) { }

        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()), pbtTarget, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.Null, "an already-populated target is skipped without exiting");
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(1, existingRoot)), "the existing state is left untouched");
    }

    private sealed class RecordingExitSource : IProcessExitSource
    {
        public int? ExitCode { get; private set; }
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) => ExitCode ??= exitCode;
    }
}
