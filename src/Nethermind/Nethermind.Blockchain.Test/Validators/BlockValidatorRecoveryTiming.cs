// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Diagnostics;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

/// <summary>
/// Mainnet experiment, not for merge: how long the Amsterdam block validator holds a payload's request thread while the
/// background sender recovery runs, with the validator recovering every missing sender (master) or only those whose
/// intrinsic gas it cannot settle without one (on demand).
/// </summary>
[TestFixture, Explicit("Timing harness for the mainnet newPayload experiment")]
public class BlockValidatorRecoveryTiming
{
    private sealed class Probe(ITxValidator txValidator, ISpecProvider specProvider)
        : BlockValidator(txValidator, Always.Valid, Always.Valid, specProvider, LimboLogs.Instance)
    {
        public bool Run(Block block, IReleaseSpec spec)
        {
            string? error = null;
            return ValidateTransactions(block, spec, ref error);
        }
    }

    [TestCase(150, 150)]
    [TestCase(430, 150)]
    [TestCase(150, 0)]
    public void Validator_loop_while_background_recovery_runs(int txCount, int headStartUs)
    {
        IReleaseSpec spec = Amsterdam.Instance;
        TestSpecProvider specProvider = new(spec);
        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        TxValidator txValidator = new(specProvider.ChainId);
        Probe probe = new(txValidator, specProvider);
        RecoverSignatures recovery = new(ecdsa, specProvider, LimboLogs.Instance);

        const int iterations = 40;
        Random random = new(7);
        PrivateKey[] keys = Enumerable.Range(0, 64).Select(_ =>
        {
            byte[] bytes = new byte[32];
            random.NextBytes(bytes);
            bytes[0] = 1;
            return new PrivateKey(bytes);
        }).ToArray();

        ulong nonce = 0;
        Transaction[] Fresh()
        {
            Transaction[] txs = new Transaction[txCount];
            for (int i = 0; i < txCount; i++)
            {
                // Unique per call, so the process-wide sender cache never answers.
                txs[i] = Build.A.Transaction
                    .WithType(TxType.EIP1559)
                    .WithChainId(specProvider.ChainId)
                    .WithNonce(nonce++)
                    .WithGasLimit(100_000)
                    .WithMaxFeePerGas(30_000_000_000)
                    .WithMaxPriorityFeePerGas(1_000_000_000)
                    .WithTo(TestItem.AddressB)
                    .WithValue(1)
                    .Signed(ecdsa, keys[i % keys.Length])
                    .TestObject;
            }
            return txs;
        }

        Dictionary<bool, List<double>> loopUs = new() { [false] = [], [true] = [] };
        Dictionary<bool, List<long>> validatorRecoveries = new() { [false] = [], [true] = [] };
        bool previous = MainnetExperiment.BlockValidatorRecoversOnDemand;
        try
        {
            for (int iteration = 0; iteration < iterations * 2; iteration++)
            {
                bool onDemand = iteration % 2 == 1;
                Transaction[] txs = Fresh();
                Block block = Build.A.Block.WithNumber(1).WithGasLimit(60_000_000).WithTransactions(txs).TestObject;
                MainnetExperiment.BlockValidatorRecoversOnDemand = onDemand;

                // The request thread starts recovery, then checks the hash, finds the parent and so on before it validates.
                recovery.StartRecovery(Keccak.Compute(BitConverter.GetBytes(iteration)), txs, spec);
                long spinUntil = Stopwatch.GetTimestamp() + headStartUs * Stopwatch.Frequency / 1_000_000;
                while (Stopwatch.GetTimestamp() < spinUntil) Thread.SpinWait(20);

                int nullBefore = txs.Count(static t => t.SenderAddress is null);
                long start = Stopwatch.GetTimestamp();
                Assert.That(probe.Run(block, spec), Is.True);
                long elapsed = Stopwatch.GetTimestamp() - start;

                // Let the background recovery finish before the next block.
                SpinWait.SpinUntil(() => txs.All(static t => t.SenderAddress is not null), 2_000);
                if (iteration >= 4) // warm-up
                {
                    loopUs[onDemand].Add(elapsed * 1_000_000.0 / Stopwatch.Frequency);
                    validatorRecoveries[onDemand].Add(nullBefore);
                }
                Thread.Sleep(20);
            }
        }
        finally
        {
            MainnetExperiment.BlockValidatorRecoversOnDemand = previous;
        }

        static double Median(List<double> values)
        {
            List<double> sorted = values.OrderBy(static v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        foreach (bool onDemand in new[] { false, true })
        {
            TestContext.Out.WriteLine(
                $"txs={txCount} headStartUs={headStartUs} mode={(onDemand ? "on-demand" : "master")} " +
                $"loop median={Median(loopUs[onDemand]):F0}us mean={loopUs[onDemand].Average():F0}us " +
                $"p90={loopUs[onDemand].OrderBy(static v => v).ElementAt(loopUs[onDemand].Count * 9 / 10):F0}us " +
                $"senders-missing-at-validation mean={validatorRecoveries[onDemand].Average():F0} workers={Environment.ProcessorCount / 2}");
        }
    }
}
