// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Init;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class InvalidBlockDiagnosticsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Invalid_block_is_rejected_without_waiting_for_its_diagnostics([Values] bool shutDownMidDump)
    {
        using ManualResetEventSlim replayEntered = new();
        using ManualResetEventSlim releaseReplay = new();
        GatedEnvFactory? gatedFactory = null;
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .Intercept<IInitConfig>(static initConfig => initConfig.AutoDump = DumpOptions.Receipts | DumpOptions.Parity | DumpOptions.Geth)
            .AddDecorator<IOverridableEnvFactory>((_, inner) => gatedFactory = new GatedEnvFactory(inner, replayEntered, releaseReplay)));
        Block head = await chain.AddBlock();
        // A transaction makes the replay reach the per-transaction cancellation check.
        Transaction tx = Build.A.Transaction.WithGasPrice(head.BaseFeePerGas).SignedAndResolved(TestItem.PrivateKeyB).To(TestItem.AddressC).TestObject;
        Block invalid = BuildInvalidChild(head, Keccak.Zero, tx);
        Block skipped = BuildInvalidChild(head, TestItem.KeccakA);
        string[] dumps = DumpFiles(invalid);
        string[] skippedDumps = DumpFiles(skipped);
        DeleteAll(dumps.Concat(skippedDumps));

        using CountdownEvent rejected = new(2);
        chain.MainProcessingContext.BlockProcessingQueue.BlockRemoved += (_, e) =>
        {
            if ((e.BlockHash == invalid.Hash || e.BlockHash == skipped.Hash) && e.ProcessingResult != ProcessingResult.Success) rejected.Signal();
        };

        InvalidBlockDiagnostics diagnostics = chain.Container.Resolve<InvalidBlockDiagnostics>();
        try
        {
            gatedFactory!.Armed = true;
            chain.BlockTree.SuggestBlock(invalid);
            Assert.That(replayEntered.Wait(Wait), Is.True, "the replay should start");
            chain.BlockTree.SuggestBlock(skipped);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(rejected.Wait(Wait), Is.True, "the rejections should not wait for the blocked replay");
                Assert.That(dumps.Any(File.Exists), Is.False);
            }

            if (shutDownMidDump)
            {
                Task shutdown = Task.Run(diagnostics.Dispose);
                Assert.That(shutdown.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "shutdown should let the dump finish");
                releaseReplay.Set();
                Assert.That(shutdown.Wait(Wait), Is.True);
                Assert.That(dumps.All(IsWritten), Is.True);
            }
            else
            {
                releaseReplay.Set();
                Assert.That(() => dumps.All(IsWritten), Is.True.After((int)Wait.TotalMilliseconds, 50));
                diagnostics.Dispose();
            }

            using JsonDocument receipts = JsonDocument.Parse(File.ReadAllText(dumps[0]));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipts.RootElement.GetArrayLength(), Is.EqualTo(1));
                Assert.That(gatedFactory.Created, Is.EqualTo(1), "a block rejected during a dump should not start another");
                Assert.That(skippedDumps.Any(File.Exists), Is.False);
            }
        }
        finally
        {
            releaseReplay.Set();
            DeleteAll(dumps.Concat(skippedDumps));
        }
    }

    private static Block BuildInvalidChild(Block parent, Hash256 stateRoot, params Transaction[] transactions) =>
        Build.A.Block.WithParent(parent).WithGasLimit(parent.GasLimit).WithBaseFeePerGas(parent.BaseFeePerGas)
            .WithTransactions(transactions).WithStateRoot(stateRoot).TestObject;

    private static string[] DumpFiles(Block block) =>
        [.. new[] { "receipts", "parityStyle", "gethStyle" }.Select(prefix => Path.Combine(Path.GetTempPath(), $"{prefix}_{block.Hash}_failed.txt"))];

    private static void DeleteAll(IEnumerable<string> paths)
    {
        foreach (string path in paths) File.Delete(path);
    }

    private static bool IsWritten(string path)
    {
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return file.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Holds the first env created once armed until released, standing in for a long replay.</summary>
    private sealed class GatedEnvFactory(IOverridableEnvFactory inner, ManualResetEventSlim entered, ManualResetEventSlim release) : IOverridableEnvFactory
    {
        private int _created;
        public volatile bool Armed;

        public int Created => Volatile.Read(ref _created);

        public IOverridableEnv Create()
        {
            if (Armed)
            {
                Interlocked.Increment(ref _created);
                entered.Set();
                release.Wait();
            }

            return inner.Create();
        }
    }
}
