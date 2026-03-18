// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Api;
using Nethermind.Db;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.TxPool;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures engine_newPayload end-to-end latency through the full handler pipeline.
/// Produces blocks on chain A (setup), then replays on fresh chain B (measurement).
/// Only the engine_newPayloadV4 call is timed — block production is excluded.
///
/// Usage:
///   dotnet run -c Release -- --newpayload [--backend Trie|FlatState] [--blocks 200] [--warmup 50]
/// </summary>
public static class NewPayloadBenchmark
{
    private const int DefaultBlocks = 300;
    private const int DefaultWarmupBlocks = 80;

    private static readonly Address Erc20Address = Address.FromNumber(0x1000);
    private static readonly Address SwapAddress = Address.FromNumber(0x2000);
    private static readonly Address WriteHeavyAddress = Address.FromNumber(0x3000);
    private static readonly Address ReadHeavyAddress = Address.FromNumber(0x4000);
    private static readonly Address MixedStorageAddress = Address.FromNumber(0x5000);
    private static readonly Address SimpleContractAddress = TestItem.AddressB;

    private static readonly byte[] SimpleContractCode = Prepare.EvmCode
        .PushData(0x01).Op(Instruction.STOP).Done;

    private static readonly byte[] StopCode = [0x00];

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressC)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddStorage(new UInt256(2))
        .Build();

    private record PayloadData(ExecutionPayloadV3 Payload, byte[][]? ExecutionRequests);

    public static async Task Run(string[] args)
    {
        StateBackend backend = StateBackend.Trie;
        int blockCount = DefaultBlocks;
        int warmupBlocks = DefaultWarmupBlocks;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--backend" when i + 1 < args.Length:
                    backend = Enum.Parse<StateBackend>(args[++i], ignoreCase: true);
                    break;
                case "--blocks" when i + 1 < args.Length:
                    blockCount = int.Parse(args[++i]);
                    break;
                case "--warmup" when i + 1 < args.Length:
                    warmupBlocks = int.Parse(args[++i]);
                    break;
            }
        }

        int totalBlocks = warmupBlocks + blockCount;
        Console.WriteLine($"NewPayload Benchmark: backend={backend}, measured={blockCount}, warmup={warmupBlocks}");
        Console.WriteLine();

        // Phase 1: Produce all payloads on chain A (same backend — state roots must match)
        Console.WriteLine("Phase 1: Producing payloads...");
        PayloadData[] payloads = await ProducePayloads(totalBlocks, backend);
        Console.WriteLine($"  Produced {payloads.Length} payloads");

        // Phase 2: Replay on fresh chain B — only engine_newPayloadV4 is timed
        Console.WriteLine($"Phase 2: Replaying on fresh {backend} chain...");
        double[] timingsMs = await Replay(payloads, backend, warmupBlocks);

        ReportStatistics(timingsMs);
    }

    // ── Phase 1: Produce payloads ────────────────────────────────────────

    private static async Task<PayloadData[]> ProducePayloads(int blockCount, StateBackend backend)
    {
        PrivateKey senderKey = TestItem.PrivateKeyA;

        using BenchmarkMergeBlockchain chain = new();
        await chain.BuildChain(BuildChainConfigurer(backend));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 headHash = chain.BlockTree.HeadHash;
        ulong timestamp = chain.BlockTree.Head!.Timestamp;
        int nonce = 0;

        List<PayloadData> payloads = new(blockCount);

        for (int b = 0; b < blockCount; b++)
        {
            timestamp += 12;
            (List<Transaction> txs, int nextNonce) = BuildMixedTransactions(senderKey, nonce, b);
            nonce = nextNonce;

            foreach (Transaction tx in txs)
                chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

            ForkchoiceStateV1 fcState = new(headHash, headHash, headHash);
            PayloadAttributes attrs = new()
            {
                Timestamp = timestamp,
                PrevRandao = Keccak.Zero,
                SuggestedFeeRecipient = Address.Zero,
                ParentBeaconBlockRoot = Keccak.Zero,
                Withdrawals = Array.Empty<Withdrawal>()
            };

            ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult =
                await rpc.engine_forkchoiceUpdatedV3(fcState, attrs);
            if (fcuResult.Data?.PayloadId is null)
            {
                Console.WriteLine($"  FCU failed at block {b + 1}. Status={fcuResult.Data?.PayloadStatus?.Status} Error={fcuResult.ErrorCode} Head=#{chain.BlockTree.Head?.Number}");
                break;
            }

            await Task.Delay(200);
            ResultWrapper<GetPayloadV5Result?> getResult =
                await rpc.engine_getPayloadV5(Convert.FromHexString(fcuResult.Data.PayloadId[2..]));
            if (getResult.Data is null) break;

            ExecutionPayloadV3 payload = getResult.Data.ExecutionPayload;
            byte[][]? execRequests = getResult.Data.ExecutionRequests;

            await rpc.engine_newPayloadV4(payload, Array.Empty<byte[]?>(), payload.ParentBeaconBlockRoot, execRequests);
            await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash));

            // Wait for head to advance before producing next block
            int waitMs = 0;
            while (chain.BlockTree.Head?.Number < b + 1 && waitMs < 30_000)
            {
                await Task.Delay(10);
                waitMs += 10;
            }

            if (chain.BlockTree.Head?.Number < b + 1)
            {
                Console.WriteLine($"  Timeout waiting for block {b + 1} to process. Head=#{chain.BlockTree.Head?.Number}");
                break;
            }

            headHash = chain.BlockTree.HeadHash;

            long chainNonce = (long)chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, senderKey.Address);
            nonce = (int)chainNonce;

            payloads.Add(new PayloadData(payload, execRequests));

            if (b == 0)
                Console.WriteLine($"  Block 1: gasUsed={payload.GasUsed}");
            if ((b + 1) % 25 == 0)
                Console.WriteLine($"  Produced {b + 1}/{blockCount}");
        }

        return payloads.ToArray();
    }

    // ── Phase 2: Replay and measure ──────────────────────────────────────

    private static async Task<double[]> Replay(PayloadData[] payloads, StateBackend backend, int warmupBlocks)
    {
        using BenchmarkMergeBlockchain chain = new();
        await chain.BuildChain(BuildChainConfigurer(backend));

        IEngineRpcModule rpc = chain.EngineRpcModule;

        // Warmup: replay first N blocks without timing
        for (int i = 0; i < warmupBlocks && i < payloads.Length; i++)
        {
            ExecutionPayloadV3 p = payloads[i].Payload;
            await rpc.engine_newPayloadV4(p, Array.Empty<byte[]?>(), p.ParentBeaconBlockRoot, payloads[i].ExecutionRequests);
            await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(p.BlockHash, p.BlockHash, p.BlockHash));
        }

        Console.WriteLine($"  Warmup done ({warmupBlocks} blocks). Head=#{chain.BlockTree.Head?.Number}");

        // Force GC and switch to low-latency mode
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GCLatencyMode oldMode = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Pin to single core to reduce OS scheduler jitter
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);

        // Measure — only engine_newPayloadV4 is timed, not forkchoiceUpdated
        List<double> timings = new(Math.Max(0, payloads.Length - warmupBlocks));

        for (int i = warmupBlocks; i < payloads.Length; i++)
        {
            ExecutionPayloadV3 p = payloads[i].Payload;

            // ── TIMED: only newPayloadV4 ──
            long sw = Stopwatch.GetTimestamp();
            ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV4(
                p, Array.Empty<byte[]?>(), p.ParentBeaconBlockRoot, payloads[i].ExecutionRequests);
            double elapsedMs = Stopwatch.GetElapsedTime(sw).TotalMilliseconds;

            // Advance head (untimed)
            await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(p.BlockHash, p.BlockHash, p.BlockHash));

            string status = result.Data?.Status ?? "null";
            timings.Add(elapsedMs);

            if (i == warmupBlocks || (i + 1) % 50 == 0 || i == payloads.Length - 1)
                Console.WriteLine($"  [MEASURE] Block {i + 1}/{payloads.Length}: {elapsedMs:F2}ms status={status} gasUsed={p.GasUsed} head=#{chain.BlockTree.Head?.Number}");
        }

        GCSettings.LatencyMode = oldMode;
        Console.WriteLine($"  Final head=#{chain.BlockTree.Head?.Number} (expected #{payloads.Length})");

        return timings.ToArray();
    }

    // ── Genesis seeding ──────────────────────────────────────────────────

    private static void SeedGenesis(Block block, IWorldState ws, ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        block.Header.GasLimit = 60_000_000;

        ws.AddToBalanceAndCreateIfNotExists(TestItem.PrivateKeyA.Address, UInt256.MaxValue / 2, spec);

        Console.Write("    Seeding state...");
        Random rng = new(42);
        byte[] addrBuf = new byte[20];
        byte[] valBuf = new byte[32];

        for (int a = 0; a < 1_000_000; a++)
        {
            rng.NextBytes(addrBuf);
            ws.AddToBalanceAndCreateIfNotExists(new Address(addrBuf.ToArray()), (UInt256)(rng.Next(1, 1_000_000)), spec);
        }

        for (int a = 0; a < 5_000; a++)
        {
            rng.NextBytes(addrBuf);
            Address addr = new(addrBuf.ToArray());
            ws.AddToBalanceAndCreateIfNotExists(addr, 1, spec);
            ws.InsertCode(addr, StopCode, spec);
            for (int s = 0; s < 100; s++)
            {
                rng.NextBytes(valBuf);
                ws.Set(new StorageCell(addr, (UInt256)rng.NextInt64()), valBuf.ToArray());
            }
        }
        Console.WriteLine(" done (1M accounts, 500k storage slots)");

        ws.CreateAccount(SimpleContractAddress, UInt256.Zero);
        ws.InsertCode(SimpleContractAddress, SimpleContractCode, spec);
        ws.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
        ws.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, spec);
        ws.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
        ws.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, spec);

        ws.CreateAccount(Erc20Address, UInt256.Zero);
        ws.InsertCode(Erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), spec);
        UInt256 senderSlot = StorageBenchmarkContracts.ComputeMappingSlot(TestItem.PrivateKeyA.Address, UInt256.Zero);
        byte[] bigBalance = new byte[32]; ((UInt256)1_000_000_000).ToBigEndian(bigBalance);
        ws.Set(new StorageCell(Erc20Address, senderSlot), bigBalance);
        byte[] recipientBal = new byte[32]; ((UInt256)100).ToBigEndian(recipientBal);
        for (int i = 0; i < 200; i++)
            ws.Set(new StorageCell(Erc20Address, StorageBenchmarkContracts.ComputeMappingSlot(Address.FromNumber((UInt256)(100 + i)), UInt256.Zero)), recipientBal);

        UInt256 swapErc20Slot = StorageBenchmarkContracts.ComputeMappingSlot(SwapAddress, UInt256.Zero);
        byte[] swapErc20Bal = new byte[32]; ((UInt256)1_000_000_000).ToBigEndian(swapErc20Bal);
        ws.Set(new StorageCell(Erc20Address, swapErc20Slot), swapErc20Bal);

        ws.CreateAccount(SwapAddress, UInt256.Zero);
        ws.InsertCode(SwapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(Erc20Address), spec);
        SeedSlot(ws, SwapAddress, 0, 1_000_000_000); SeedSlot(ws, SwapAddress, 1, 1_000_000_000);
        SeedSlot(ws, SwapAddress, 2, 500_000); SeedSlot(ws, SwapAddress, 3, 30);
        SeedSlot(ws, SwapAddress, 4, 1); SeedSlot(ws, SwapAddress, 5, 1);
        SeedSlot(ws, SwapAddress, 6, 1); SeedSlot(ws, SwapAddress, 7, 1_000_000_000);
        UInt256 senderSwapSlot = StorageBenchmarkContracts.ComputeMappingSlot(TestItem.PrivateKeyA.Address, (UInt256)8);
        byte[] swapBal = new byte[32]; ((UInt256)1_000_000).ToBigEndian(swapBal);
        ws.Set(new StorageCell(SwapAddress, senderSwapSlot), swapBal);

        ws.CreateAccount(WriteHeavyAddress, UInt256.Zero);
        ws.InsertCode(WriteHeavyAddress, StorageBenchmarkContracts.BuildStorageWriteHeavyCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, WriteHeavyAddress, s, 1);

        ws.CreateAccount(ReadHeavyAddress, UInt256.Zero);
        ws.InsertCode(ReadHeavyAddress, StorageBenchmarkContracts.BuildStorageReadHeavyCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, ReadHeavyAddress, s, (ulong)(s + 1) * 1000);

        ws.CreateAccount(MixedStorageAddress, UInt256.Zero);
        ws.InsertCode(MixedStorageAddress, StorageBenchmarkContracts.BuildStorageMixedCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, MixedStorageAddress, s, (ulong)(s + 1) * 100);
    }

    private static void SeedSlot(IWorldState ws, Address addr, int slot, ulong value)
    {
        byte[] bytes = new byte[32]; ((UInt256)value).ToBigEndian(bytes);
        ws.Set(new StorageCell(addr, (UInt256)slot), bytes);
    }

    // ── Chain configuration ──────────────────────────────────────────────

    private static Action<ContainerBuilder> BuildChainConfigurer(StateBackend backend)
    {
        return builder =>
        {
            builder.AddSingleton<ISpecProvider>(CreateOsakaSpecProvider());
            builder.WithGenesisPostProcessor(SeedGenesis);
            if (backend == StateBackend.Trie)
                builder.Intercept<IFlatDbConfig>(cfg => cfg.Enabled = false);
        };
    }

    private static ISpecProvider CreateOsakaSpecProvider()
    {
        TestSingleReleaseSpecProvider provider = new(Osaka.Instance);
        provider.TimestampFork = 0;
        provider.TerminalTotalDifficulty = 0;
        provider.UpdateMergeTransitionInfo(0);
        return provider;
    }

    // ── Transaction building ─────────────────────────────────────────────

    private static (List<Transaction> txs, int nextNonce) BuildMixedTransactions(PrivateKey senderKey, int nonce, int blockIndex)
    {
        List<Transaction> txs = new(300);

        for (int t = 0; t < 50; t++)
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, TestItem.AddressC, (UInt256)1, 21_000));

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[64];
            Address.FromNumber((UInt256)(100 + (blockIndex * 50 + t) % 200)).Bytes.CopyTo(calldata.AsSpan(12));
            ((UInt256)1).ToBigEndian(calldata.AsSpan(32));
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, Erc20Address, UInt256.Zero, 100_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 1)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, SwapAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 42)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, WriteHeavyAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, ReadHeavyAddress, UInt256.Zero, 100_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 7)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, MixedStorageAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 15; t++)
        {
            txs.Add(Build.A.Transaction
                .WithType(TxType.AccessList).WithNonce((UInt256)nonce++).WithTo(TestItem.AddressC)
                .WithValue((UInt256)1).WithGasLimit(50_000).WithGasPrice(20.GWei)
                .WithAccessList(SampleAccessList).SignedAndResolved(senderKey).TestObject);
        }

        for (int t = 0; t < 5; t++)
        {
            byte[] code = Prepare.EvmCode.PushData(0x01).Op(Instruction.STOP).Done;
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, null, UInt256.Zero, 100_000, code));
        }

        for (int t = 0; t < 5; t++)
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, SimpleContractAddress, UInt256.Zero, 50_000));

        return (txs, nonce);
    }

    private static Transaction BuildEip1559Tx(PrivateKey key, ref int nonce, Address? to, UInt256 value, long gasLimit, byte[]? data = null)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(TxType.EIP1559).WithNonce((UInt256)nonce++).WithTo(to).WithValue(value)
            .WithGasLimit(gasLimit).WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei);
        if (data is not null) builder = builder.WithData(data);
        return builder.SignedAndResolved(key).TestObject;
    }

    // ── Reporting ────────────────────────────────────────────────────────

    private static void ReportStatistics(double[] timingsMs)
    {
        if (timingsMs.Length == 0) { Console.WriteLine("No timings."); return; }

        Array.Sort(timingsMs);
        double mean = timingsMs.Average();
        double median = Percentile(timingsMs, 50);
        double stddev = Math.Sqrt(timingsMs.Select(t => (t - mean) * (t - mean)).Average());
        double cv = mean > 0 ? (stddev / mean) * 100 : 0;

        int trimCount = (int)(timingsMs.Length * 0.05);
        double[] trimmed = timingsMs.Skip(trimCount).Take(timingsMs.Length - 2 * trimCount).ToArray();
        double trimmedMean = trimmed.Length > 0 ? trimmed.Average() : mean;
        double trimmedStddev = trimmed.Length > 1
            ? Math.Sqrt(trimmed.Select(t => (t - trimmedMean) * (t - trimmedMean)).Average()) : 0;
        double trimmedCv = trimmedMean > 0 ? (trimmedStddev / trimmedMean) * 100 : 0;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine("  engine_newPayload Benchmark Results");
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.WriteLine($"  Blocks:       {timingsMs.Length}");
        Console.WriteLine($"  Mean:         {mean:F3} ms");
        Console.WriteLine($"  Median:       {median:F3} ms");
        Console.WriteLine($"  StdDev:       {stddev:F3} ms");
        Console.WriteLine($"  CV:           {cv:F1}%");
        Console.WriteLine($"  ─── Trimmed (5%-95%) ───");
        Console.WriteLine($"  TrimmedMean:  {trimmedMean:F3} ms");
        Console.WriteLine($"  TrimmedSD:    {trimmedStddev:F3} ms");
        Console.WriteLine($"  TrimmedCV:    {trimmedCv:F1}%");
        Console.WriteLine($"  ─── Distribution ───");
        Console.WriteLine($"  Min:          {timingsMs[0]:F3} ms");
        Console.WriteLine($"  P5:           {Percentile(timingsMs, 5):F3} ms");
        Console.WriteLine($"  P95:          {Percentile(timingsMs, 95):F3} ms");
        Console.WriteLine($"  P99:          {Percentile(timingsMs, 99):F3} ms");
        Console.WriteLine($"  Max:          {timingsMs[^1]:F3} ms");
        Console.WriteLine("═══════════════════════════════════════════════════════");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double index = (percentile / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        double frac = index - lower;
        return sorted[lower] * (1 - frac) + sorted[upper] * frac;
    }

    // ── Helper types ─────────────────────────────────────────────────────

    private sealed class BenchmarkMergeBlockchain : BaseEngineModuleTests.MergeTestBlockchain
    {
        protected override ChainSpec CreateChainSpec() =>
            new() { Genesis = Core.Test.Builders.Build.A.Block.WithDifficulty(0).WithGasLimit(60_000_000).TestObject };

        protected override AutoCancelTokenSource CreateCancellationSource() =>
            AutoCancelTokenSource.ThatCancelAfter(TimeSpan.FromMinutes(2));

        public Task<BenchmarkMergeBlockchain> BuildChain(Action<ContainerBuilder>? configurer) =>
            BuildMergeTestBlockchain(configurer ?? (_ => { })).ContinueWith(t => (BenchmarkMergeBlockchain)t.Result);
    }
}
