// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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
            .Intercept<IInitConfig>(static initConfig => initConfig.AutoDump = DumpOptions.Receipts)
            .AddDecorator<IOverridableEnvFactory>((_, inner) => gatedFactory = new GatedEnvFactory(inner, replayEntered, releaseReplay)));
        Block head = await chain.AddBlock();
        Block invalid = Build.A.Block.WithParent(head).WithGasLimit(head.GasLimit).WithBaseFeePerGas(head.BaseFeePerGas)
            .WithStateRoot(Keccak.Zero).TestObject;
        string receiptsDump = Path.Combine(Path.GetTempPath(), $"receipts_{invalid.Hash}_failed.txt");
        File.Delete(receiptsDump);

        using ManualResetEventSlim rejected = new();
        chain.MainProcessingContext.BlockProcessingQueue.BlockRemoved += (_, e) =>
        {
            if (e.BlockHash == invalid.Hash && e.ProcessingResult != ProcessingResult.Success) rejected.Set();
        };

        try
        {
            gatedFactory!.Armed = true;
            chain.BlockTree.SuggestBlock(invalid);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayEntered.Wait(Wait), Is.True, "the replay should start");
                Assert.That(rejected.Wait(Wait), Is.True, "the rejection should not wait for the blocked replay");
                Assert.That(File.Exists(receiptsDump), Is.False);
            }

            if (shutDownMidDump)
            {
                Task shutdown = Task.Run(chain.Container.Resolve<InvalidBlockDiagnostics>().Dispose);
                Assert.That(shutdown.Wait(TimeSpan.FromMilliseconds(200)), Is.False, "shutdown should let the dump finish");
                releaseReplay.Set();
                Assert.That(shutdown.Wait(Wait), Is.True);
                Assert.That(IsWritten(receiptsDump), Is.True);
            }
            else
            {
                releaseReplay.Set();
                Assert.That(() => IsWritten(receiptsDump), Is.True.After((int)Wait.TotalMilliseconds, 50));
            }
        }
        finally
        {
            releaseReplay.Set();
            File.Delete(receiptsDump);
        }
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
        public volatile bool Armed;

        public IOverridableEnv Create()
        {
            if (Armed)
            {
                entered.Set();
                release.Wait();
            }

            return inner.Create();
        }
    }
}
