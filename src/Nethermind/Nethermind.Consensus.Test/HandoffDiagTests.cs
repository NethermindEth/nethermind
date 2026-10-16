// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// BENCH ONLY (bench/handoff-diag2): the slow-block fields of the handoff diagnostic for a block processed after it was warmed.
/// Run once as is and once with BENCH_HANDOFF_DIAG_FAIL=1, where the block must still end in the state of executing it.
/// With HANDOFF_DIAG_OUT set, the JSON is appended there as a slow-block log line, for opt/handoff_residual_check.py.
/// </summary>
[NonParallelizable]
[TestFixture(true)]
[TestFixture(false)]
public class HandoffDiagTests(bool earlyStart) : PrewarmerHandoffTestBase(Osaka.Instance, earlyStart)
{
    private const double Slack = 0.002;

    [Test]
    public void A_block_processed_with_the_diag_on_prints_its_timeline_and_runs()
    {
        Assume.That(HandoffDiag.Enabled, "BENCH_HANDOFF_DIAG=0");
        Block block = DiagBlock();
        Run executed = Process(block, adapter: null);
        RunPreWarmCaches(PreWarmer, block);
        Run run = ProcessWithDiag(block);
        HandoffDiag.BlockRecords? records = HandoffDiag.TakeRecords();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        AssertSameReceipts(run.Receipts, executed.Receipts);
        Assert.That(records, Is.Not.Null);
        string json = Report(records!, block);
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HANDOFF_DIAG_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, $"2026-10-16 00:00:00.0000|WARN|SlowBlocks|1|{json}\n");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement handoff = root.GetProperty("handoff");
        JsonElement[] txs = [.. root.GetProperty("transactions").EnumerateArray()];
        bool failTest = records!.FailTest;
        bool warmFailTest = records.Footprints!.Diag.FailTest;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(txs, Has.Length.EqualTo(block.Transactions.Length));
            Assert.That(Number(handoff, "prep_us"), Is.GreaterThanOrEqualTo(0));
            Assert.That(Number(handoff, "diag_us_blk"), Is.GreaterThanOrEqualTo(0));
            if (failTest)
            {
                Assert.That(handoff.GetProperty("diag_hooks").GetString(), Does.StartWith("prepare,arrive"));
                Assert.That(Number(handoff, "diag_errors"), Is.EqualTo(handoff.GetProperty("diag_hooks").GetString()!.Split(',').Length));
                Assert.That(Number(handoff, "report_errors"), Is.EqualTo(2));
                Assert.That(txs[2].TryGetProperty("handoff", out _), Is.False);
                Assert.That(handoff.GetProperty("report_error").GetString(), Does.Contain("BENCH_HANDOFF_DIAG_FAIL report"));
            }
            else
            {
                Assert.That(Number(handoff, "diag_errors"), Is.Zero);
                Assert.That(Number(handoff, "report_errors"), Is.Zero);
            }

            for (int i = 0; i < txs.Length; i++)
            {
                if (failTest && i == 2) continue;
                Assert.That(txs[i].TryGetProperty("handoff", out _), Is.True, $"handoff of {i}");
                Assert.That(Number(txs[i], "gl"), Is.EqualTo((double)block.Transactions[i].GasLimit), $"gl of {i}");
            }
        }

        double prewarm = Number(handoff, "t_prewarm");
        double load1 = Number(handoff, "t_load1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prewarm, Is.Negative, "PreWarmCaches runs before the transactions");
            Assert.That(Number(handoff, "t_session"), Is.GreaterThanOrEqualTo(prewarm - Slack));
            Assert.That(Number(handoff, "t_addr_end"), Is.GreaterThanOrEqualTo(Number(handoff, "t_addr0") - Slack));
            Assert.That(Number(handoff, "workers_run"), Is.GreaterThanOrEqualTo(1));
            Assert.That(handoff.GetProperty("t_worker").GetArrayLength(), Is.EqualTo((int)Number(handoff, "workers")));
            if (warmFailTest)
            {
                Assert.That(Number(handoff, "warm_faults"), Is.EqualTo(1));
                Assert.That(handoff.GetProperty("warm_fault").GetString(), Does.Contain("BENCH_HANDOFF_DIAG_FAIL load"));
                Assert.That(handoff.TryGetProperty("jobs", out _), Is.False);
            }
            else
            {
                Assert.That(Number(handoff, "warm_faults"), Is.Zero);
                Assert.That(load1, Is.GreaterThanOrEqualTo(Number(handoff, "t_load0") - Slack));
                Assert.That(Number(handoff, "jobs"), Is.GreaterThanOrEqualTo(4));
                Assert.That(Number(handoff, "hoisted"), Is.EqualTo(1));
                Assert.That(Number(handoff, "split"), Is.EqualTo(2));
                Assert.That(Number(handoff, "unclaimed_at_load"), Is.Zero);
                Assert.That(Number(handoff, "recov_at_load"), Is.EqualTo(block.Transactions.Length));
                Assert.That(txs[4].GetProperty("job").GetString(), Is.EqualTo("h"));
                Assert.That(txs[5].GetProperty("job").GetString(), Is.EqualTo("s"));
                Assert.That(txs[0].GetProperty("job").GetString(), Is.EqualTo("g"));
                Assert.That(Number(txs[4], "rank"), Is.Zero, "the hoisted job is taken first");
            }

            int counted = 0, executedRows = 0;
            for (int i = 0; i < txs.Length; i++)
            {
                if (failTest && i == 2) continue;
                JsonElement tx = txs[i];
                Assert.That(tx.TryGetProperty("w0", out JsonElement w0), Is.True, $"w0 of {i}");
                double start = w0.GetDouble();
                Assert.That(start, Is.GreaterThanOrEqualTo(prewarm - Slack), $"w0 of {i}");
                if (!warmFailTest) Assert.That(start, Is.GreaterThanOrEqualTo(load1 - Slack), $"w0 of {i} after the load");
                Assert.That(Number(tx, "w1"), Is.GreaterThanOrEqualTo(start - Slack), $"w1 of {i}");
                Assert.That(Number(tx, "w1"), Is.Negative, $"warm of {i} ended before the transactions");
                Assert.That(tx.GetProperty("w_state").GetString(), Is.Not.Empty);
                Assert.That(Number(tx, "w_cold_s"), Is.LessThanOrEqualTo(Number(tx, "w_cold")), $"w_cold of {i}");
                Assert.That(Number(tx, "slot"), Is.GreaterThanOrEqualTo(0), $"slot of {i}");
                if (tx.TryGetProperty("w_ops", out JsonElement ops))
                {
                    counted++;
                    Assert.That(ops.GetDouble(), Is.GreaterThanOrEqualTo(0));
                    Assert.That(Number(tx, "w_sload"), Is.GreaterThanOrEqualTo(0));
                }

                if (tx.TryGetProperty("m_ops", out JsonElement mainOps))
                {
                    executedRows++;
                    Assert.That(tx.GetProperty("handoff").GetString(), Is.Not.EqualTo("taken"), $"m_ops of {i}");
                    Assert.That(mainOps.GetDouble(), Is.GreaterThanOrEqualTo(0));
                    Assert.That(Number(tx, "m_cold_s"), Is.LessThanOrEqualTo(Number(tx, "m_cold")), $"m_cold of {i}");
                }
            }

            Assert.That(counted, Is.GreaterThan(0), "recorded runs count their opcodes");
            Assert.That(executedRows, Is.GreaterThan(0), "the unfunded sender's transfer is executed");
            JsonElement unfunded = txs[^1];
            Assert.That(unfunded.GetProperty("handoff").GetString(), Is.EqualTo("missing"));
            Assert.That(unfunded.GetProperty("w_state").GetString(), Does.StartWith("failed"));
            Assert.That(Number(unfunded, "m_ops"), Is.Zero, "a transfer runs no opcodes");
            if (!warmFailTest) Assert.That(Number(unfunded, "act"), Is.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    public void A_block_with_mempool_runs_prints_where_its_seed_ran()
    {
        Assume.That(HandoffDiag.Enabled, "BENCH_HANDOFF_DIAG=0");
        Block block = BuildBlock(
            Call(TestItem.PrivateKeyA, 0, Counter),
            Transfer(TestItem.PrivateKeyB, 0, TestItem.AddressC, 1.Wei),
            Call(TestItem.PrivateKeyD, 0, Logger));
        Run executed = Process(block, adapter: null);
        PreWarmer.RunSpeculativePreWarm(Parent, Spec, Predicted(block), () => PreWarmer.SpeculativeMarkerPublished);
        RunPreWarmCaches(PreWarmer, block);
        Run run = ProcessWithDiag(block);
        HandoffDiag.BlockRecords records = HandoffDiag.TakeRecords()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Results, Is.EqualTo(executed.Results));
            Assert.That(run.StateRoot, Is.EqualTo(executed.StateRoot));
        }

        string json = Report(records, block);
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HANDOFF_DIAG_OUT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, $"2026-10-16 00:00:00.0000|WARN|SlowBlocks|2|{json}\n");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement handoff = document.RootElement.GetProperty("handoff");
        JsonElement[] txs = [.. document.RootElement.GetProperty("transactions").EnumerateArray()];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handoff.GetProperty("b1").GetProperty("early_start").GetBoolean(), Is.EqualTo(EarlyStart));
            Assert.That(handoff.GetProperty("b1").GetProperty("workers_first").GetBoolean(), Is.True);
            Assert.That(Number(handoff, "seed_n"), Is.GreaterThanOrEqualTo(1), "a mempool run is seeded");
            Assert.That(Number(handoff, "t_seed_end"), Is.GreaterThanOrEqualTo(Number(handoff, "t_seed0") - Slack));
            Assert.That(Number(handoff, "t_seed0"), Is.GreaterThanOrEqualTo(Number(handoff, "t_session") - Slack));
            // Ahead of the workers without the early start; beside them with it.
            if (!EarlyStart) Assert.That(Number(handoff, "t_seed_end"), Is.LessThanOrEqualTo(Number(handoff, "t_load0") + Slack));
            Assert.That(Number(handoff, "t_fanout"), Is.GreaterThanOrEqualTo(Number(handoff, "t_addr0") - Slack));
            if (EarlyStart) Assert.That(Number(handoff, "t_fanout"), Is.GreaterThanOrEqualTo(Number(handoff, "t_load1") - Slack), "the fan-out waits for the workers");
            int seeded = 0;
            foreach (JsonElement tx in txs)
            {
                if (tx.TryGetProperty("mp", out JsonElement mp) && mp.GetString() == "seeded") seeded++;
                if (!records.FailTest && !records.Footprints!.Diag.FailTest) Assert.That(tx.TryGetProperty("mp_run", out _), Is.True, $"mp_run of {tx}");
            }

            Assert.That(seeded, Is.EqualTo((int)Number(handoff, "seed_n")));
        }
    }

    // As the mempool prewarmer builds the block it predicts: copies of the block's transactions on a simulated child.
    private Block Predicted(Block block)
    {
        BlockHeader header = Parent.CreateSimulatedChild(block.Timestamp);
        header.Beneficiary = Parent.GasBeneficiary ?? Address.Zero;
        header.BaseFeePerGas = block.BaseFeePerGas;
        header.ExcessBlobGas = block.Header.ExcessBlobGas;
        Transaction[] copies = new Transaction[block.Transactions.Length];
        for (int i = 0; i < copies.Length; i++)
        {
            copies[i] = new Transaction();
            block.Transactions[i].CopyTo(copies[i], copyHash: true);
        }

        return new Block(header, new BlockBody(copies, [], null));
    }

    [Test]
    public void The_main_thread_cost_of_the_diag_stays_low_once_warm()
    {
        Assume.That(HandoffDiag.Enabled, "BENCH_HANDOFF_DIAG=0");
        Block block = DiagBlock();
        List<double> prepare = [], perExecuted = [], total = [];
        for (int i = 0; i < 60; i++)
        {
            RunPreWarmCaches(PreWarmer, block);
            ProcessWithDiag(block);
            HandoffDiag.BlockRecords records = HandoffDiag.TakeRecords()!;
            if (i < 30 || records.FailTest) continue;
            long executedTicks = 0, blockTicks = records.PrepareTicks;
            int executedCount = 0;
            foreach (HandoffDiag.TxRecord record in records.Records)
            {
                blockTicks += record.DiagTicks;
                if (!record.Counted) continue;
                executedTicks += record.DiagTicks;
                executedCount++;
            }

            prepare.Add(Us(records.PrepareTicks));
            total.Add(Us(blockTicks));
            if (executedCount > 0) perExecuted.Add(Us(executedTicks) / executedCount);
        }

        TestContext.Out.WriteLine($"diag_us over {total.Count} blocks of {block.Transactions.Length} txs: prepare p50 {Median(prepare):F1}, "
            + $"per executed tx p50 {Median(perExecuted):F1}, block p50 {Median(total):F1}");
        Assert.That(Median(total), Is.LessThan(200));
    }

    private Block DiagBlock() => BuildBlock(
        Call(TestItem.PrivateKeyA, 0, Counter),
        Call(TestItem.PrivateKeyB, 0, Counter),
        Transfer(TestItem.PrivateKeyA, 1, TestItem.AddressC, 1.Wei),
        Call(TestItem.PrivateKeyB, 1, Logger),
        // A heavy sender group: split, its first job hoisted.
        Call(TestItem.PrivateKeyC, 0, Counter, gasLimit: 5_000_000),
        Call(TestItem.PrivateKeyC, 1, Logger, gasLimit: 3_000_000),
        Call(TestItem.PrivateKeyA, 2, Reverter),
        Transfer(TestItem.PrivateKeyA, 3, Fresh, 1.Wei),
        Call(TestItem.PrivateKeyB, 2, Gift),
        // Unfunded on the parent state: its warm run fails, so the main thread executes it.
        Transfer(TestItem.PrivateKeyA, 4, TestItem.PrivateKeyE.Address, 1.Ether),
        Transfer(TestItem.PrivateKeyE, 0, TestItem.AddressD, 1.Wei));

    private Run ProcessWithDiag(Block block) => Process(block, ProductionAdapter, beforeTransaction: index =>
    {
        if (index == 0) HandoffDiag.Prepare(block);
    });

    private static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetDouble() : throw new AssertionException($"no {name} in {element}");

    // As ProcessingStats writes the per-transaction slow-block entries and the block's handoff object.
    private static string Report(HandoffDiag.BlockRecords records, Block block)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("block");
            writer.WriteNumber("number", block.Number);
            writer.WriteNumber("tx_count", block.Transactions.Length);
            writer.WriteEndObject();
            writer.WriteStartArray("transactions");
            for (int i = 0; i < block.Transactions.Length; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", i);
                HandoffDiag.WriteTransaction(writer, records, block.Transactions, i, block.Header.GasBeneficiary);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            HandoffDiag.WriteBlock(writer, records, block.Transactions);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
