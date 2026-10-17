// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class InvalidBlockDiagnosticsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Rejection_does_not_wait_for_the_diagnostics_of_the_invalid_block()
    {
        using ManualResetEventSlim replayEntered = new();
        using ManualResetEventSlim releaseReplay = new();
        GatedEnvFactory? gatedFactory = null;
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .Intercept<IInitConfig>(static initConfig => initConfig.AutoDump = DumpOptions.Receipts)
            .AddDecorator<IOverridableEnvFactory>((_, inner) => gatedFactory = new GatedEnvFactory(inner, replayEntered, releaseReplay)));
        Block produced = await chain.AddBlock();
        Block invalid = Build.A.Block.WithHeader(produced.Header.Clone()).WithStateRoot(Keccak.Zero)
            .WithTransactions(produced.Transactions).TestObject;
        string receiptsDump = Path.Combine(Path.GetTempPath(), $"receipts_{invalid.Hash}_failed.txt");
        File.Delete(receiptsDump);

        try
        {
            gatedFactory!.Armed = true;
            Task rejection = Task.Run(() => RaiseInvalidBlock(chain.MainProcessingContext.BlockProcessingQueue, invalid));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(replayEntered.Wait(Wait), Is.True, "the replay should start");
                Assert.That(rejection.Wait(Wait), Is.True, "the rejection should not wait for the blocked replay");
                Assert.That(File.Exists(receiptsDump), Is.False);
            }

            releaseReplay.Set();
            Assert.That(() => IsWritten(receiptsDump), Is.True.After((int)Wait.TotalMilliseconds, 50));
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

    private static void RaiseInvalidBlock(IBlockProcessingQueue processingQueue, Block invalid)
    {
        FieldInfo field = processingQueue.GetType().GetField(nameof(IBlockProcessingQueue.InvalidBlock), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"InvalidBlock event field not found on {processingQueue.GetType().FullName}");
        EventHandler<IBlockProcessingQueue.InvalidBlockEventArgs>? handler = (EventHandler<IBlockProcessingQueue.InvalidBlockEventArgs>?)field.GetValue(processingQueue);
        handler?.Invoke(processingQueue, new IBlockProcessingQueue.InvalidBlockEventArgs { InvalidBlock = invalid });
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
