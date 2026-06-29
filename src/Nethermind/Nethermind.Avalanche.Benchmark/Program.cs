// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>
/// Console entry point for the Avalanche C-Chain block-execution throughput benchmark. Loads a
/// contiguous range of blocks and (optionally) a pre-state, executes them through Nethermind's
/// <c>BranchProcessor</c> under Avalanche spec rules, and reports throughput aggregates.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            return RunBenchmark(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal: " + ex);
            return 2;
        }
    }

    private static int RunBenchmark(Options options)
    {
        Console.WriteLine($"Loading Avalanche chainspec from: {options.ChainSpecPath}");
        (_, ISpecProvider specProvider) = AvalancheSpecProviderFactory.Create(options.ChainSpecPath);
        Console.WriteLine($"Chain id: {specProvider.ChainId}");

        Console.WriteLine($"Loading blocks from: {options.BlocksPath}");
        IReadOnlyList<Block> blocks = BlockSource.Load(options.BlocksPath);
        Console.WriteLine($"Loaded {blocks.Count} contiguous blocks: {blocks[0].Number}..{blocks[^1].Number}");

        for (int warmup = 0; warmup < options.WarmupRounds; warmup++)
        {
            Console.WriteLine($"Warmup round {warmup + 1}/{options.WarmupRounds}...");
            using AvalancheBlockExecutionBenchmark warm = new(specProvider, options.PreStatePath);
            warm.Run(blocks);
        }

        Console.WriteLine("Measuring...");
        using AvalancheBlockExecutionBenchmark bench = new(specProvider, options.PreStatePath);
        if (bench.SeededAccountCount > 0)
        {
            Console.WriteLine($"Seeded {bench.SeededAccountCount} accounts; pre-state root {bench.SeededStateRoot}");
        }
        else
        {
            Console.WriteLine($"No pre-state seeded; starting from empty state root {bench.SeededStateRoot}");
        }

        BenchmarkResult result = bench.Run(blocks);

        PrintReport(result);

        if (!string.IsNullOrWhiteSpace(options.CsvOutPath))
        {
            WriteCsv(result, options.CsvOutPath!);
            Console.WriteLine($"Per-block CSV written to: {options.CsvOutPath}");
        }

        return result.FailedBlocks == 0 ? 0 : 3;
    }

    private static void PrintReport(BenchmarkResult r)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        Console.WriteLine();
        Console.WriteLine("===== Avalanche C-Chain block execution =====");
        Console.WriteLine($"Blocks executed:   {r.SucceededBlocks}/{r.TotalBlocks}" + (r.FailedBlocks > 0 ? $" ({r.FailedBlocks} failed)" : string.Empty));
        Console.WriteLine($"Total transactions:{r.TotalTxs.ToString("N0", c),16}");
        Console.WriteLine($"Total gas:         {r.TotalGas.ToString("N0", c),16}");
        Console.WriteLine($"Total exec time:   {r.TotalExecutionMs.ToString("N3", c),12} ms");
        Console.WriteLine("---------------------------------------------");
        Console.WriteLine($"Throughput:        {r.MGasPerSecond.ToString("N2", c),12} Mgas/s");
        Console.WriteLine($"Block rate:        {r.BlocksPerSecond.ToString("N2", c),12} blocks/s");
        Console.WriteLine("---- per-block execution time (ms) ----------");
        Console.WriteLine($"  mean: {r.MeanMs.ToString("N3", c)}   min: {r.MinMs.ToString("N3", c)}   max: {r.MaxMs.ToString("N3", c)}");
        Console.WriteLine($"  p50:  {r.P50Ms.ToString("N3", c)}   p90: {r.P90Ms.ToString("N3", c)}   p99: {r.P99Ms.ToString("N3", c)}");
        Console.WriteLine("=============================================");

        if (r.FailedBlocks > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Failed blocks (first 10 of {r.FailedBlocks}):");
            int shown = 0;
            foreach (BlockResult b in r.Blocks)
            {
                if (b.Succeeded)
                {
                    continue;
                }

                Console.WriteLine($"  #{b.Number}: {b.Error}");
                if (++shown >= 10)
                {
                    break;
                }
            }
        }
    }

    private static void WriteCsv(BenchmarkResult r, string path)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder sb = new();
        sb.AppendLine("block_number,gas_used,tx_count,exec_ms,succeeded,error");
        foreach (BlockResult b in r.Blocks)
        {
            string error = b.Error is null ? string.Empty : "\"" + b.Error.Replace("\"", "\"\"") + "\"";
            sb.Append(b.Number).Append(',')
              .Append(b.GasUsed).Append(',')
              .Append(b.TxCount).Append(',')
              .Append(b.ElapsedMs.ToString("R", c)).Append(',')
              .Append(b.Succeeded ? "1" : "0").Append(',')
              .Append(error).Append('\n');
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Avalanche C-Chain block-execution throughput benchmark");
        w.WriteLine();
        w.WriteLine("Usage:");
        w.WriteLine("  Nethermind.Avalanche.Benchmark --blocks <path> [options]");
        w.WriteLine();
        w.WriteLine("Required:");
        w.WriteLine("  --blocks <path>      Directory of *.rlp files or a single concatenated-RLP file");
        w.WriteLine();
        w.WriteLine("Options:");
        w.WriteLine("  --prestate <path>    JSON pre-state to seed (address -> {balance,nonce,code,storage})");
        w.WriteLine("  --chainspec <path>   Avalanche chainspec JSON (default: avalanche-cchain.json next to the exe)");
        w.WriteLine("  --warmup <n>         Warmup rounds before the measured run (default: 0)");
        w.WriteLine("  --out <path>         Write a per-block CSV report to <path>");
        w.WriteLine("  --help               Show this help");
    }

    private sealed class Options
    {
        public string BlocksPath = string.Empty;
        public string? PreStatePath;
        public string ChainSpecPath = string.Empty;
        public int WarmupRounds;
        public string? CsvOutPath;
        public bool ShowHelp;

        public static Options Parse(string[] args)
        {
            Options o = new();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help" or "-h":
                        o.ShowHelp = true;
                        return o;
                    case "--blocks":
                        o.BlocksPath = RequireValue(args, ref i);
                        break;
                    case "--prestate":
                        o.PreStatePath = RequireValue(args, ref i);
                        break;
                    case "--chainspec":
                        o.ChainSpecPath = RequireValue(args, ref i);
                        break;
                    case "--warmup":
                        o.WarmupRounds = int.Parse(RequireValue(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "--out":
                        o.CsvOutPath = RequireValue(args, ref i);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'.");
                }
            }

            if (o.ShowHelp)
            {
                return o;
            }

            if (string.IsNullOrWhiteSpace(o.BlocksPath))
            {
                throw new ArgumentException("--blocks is required.");
            }

            if (string.IsNullOrWhiteSpace(o.ChainSpecPath))
            {
                o.ChainSpecPath = Path.Combine(AppContext.BaseDirectory, "avalanche-cchain.json");
            }

            if (o.WarmupRounds < 0)
            {
                throw new ArgumentException("--warmup must be >= 0.");
            }

            return o;
        }

        private static string RequireValue(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Argument '{args[i]}' requires a value.");
            }

            return args[++i];
        }
    }
}
