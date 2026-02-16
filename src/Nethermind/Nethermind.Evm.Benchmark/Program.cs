// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using BenchmarkDotNet.Running;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.Benchmark;

if (args.Length > 0 && args[0] == "--diag")
{
    string pattern = args.Length > 1 ? args[1] : "*";
    RunDiagnostic(pattern);
    return;
}

int inprocessIdx = Array.IndexOf(args, "--inprocess");
if (inprocessIdx >= 0)
{
    GasBenchmarkConfig.InProcess = true;
    string[] filtered = new string[args.Length - 1];
    Array.Copy(args, 0, filtered, 0, inprocessIdx);
    Array.Copy(args, inprocessIdx + 1, filtered, inprocessIdx, args.Length - inprocessIdx - 1);
    args = filtered;
}

// Handle --mode=EVM or --mode=Block: translates to BDN filter for the corresponding benchmark class
args = ApplyModeFilter(args);

// Handle --chunk N/M: splits scenarios across runners (e.g. --chunk 2/5 means second of five chunks)
args = ApplyChunkFilter(args);

ConfigureTimingFilePath();
BenchmarkSwitcher.FromAssembly(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks).Assembly).Run(args);
GasNewPayloadBenchmarks.PrintFinalTimingBreakdown();

static void ConfigureTimingFilePath()
{
    string timingFilePath = Path.Combine(
        Path.GetTempPath(),
        $"nethermind-newpayload-timing-{Guid.NewGuid():N}.jsonl");
    string timingReportFilePath = Path.Combine(
        Directory.GetCurrentDirectory(),
        "BenchmarkDotNet.Artifacts",
        "results",
        $"newpayload-timing-breakdown-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
    Environment.SetEnvironmentVariable(GasNewPayloadBenchmarks.TimingFileEnvVar, timingFilePath);
    Environment.SetEnvironmentVariable(GasNewPayloadBenchmarks.TimingReportFileEnvVar, timingReportFilePath);
}

static string[] ApplyModeFilter(string[] args)
{
    int modeIdx = -1;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--mode=", StringComparison.OrdinalIgnoreCase) || args[i].StartsWith("--mode:", StringComparison.OrdinalIgnoreCase))
        {
            modeIdx = i;
            break;
        }
    }

    if (modeIdx < 0)
        return args;

    string modeValue = args[modeIdx].Substring(7); // after "--mode=" or "--mode:"
    string classFilter = modeValue.ToUpperInvariant() switch
    {
        "EVM" => "*GasPayload*",
        "BLOCKONE" => "*GasBlockOne*",
        "BLOCK" => "*GasBlockBenchmarks*",
        "NEWPAYLOAD" => "*GasNewPayload*",
        _ => throw new ArgumentException($"Unknown --mode value: '{modeValue}'. Expected 'EVM', 'BlockOne', 'Block', or 'NewPayload'.")
    };

    // Remove --mode from args
    string[] remaining = new string[args.Length - 1];
    Array.Copy(args, 0, remaining, 0, modeIdx);
    Array.Copy(args, modeIdx + 1, remaining, modeIdx, args.Length - modeIdx - 1);

    // If user already has --filter, combine with mode filter; otherwise add --filter
    int filterIdx = Array.IndexOf(remaining, "--filter");
    if (filterIdx >= 0 && filterIdx + 1 < remaining.Length)
    {
        // Wrap the existing filter with the mode class prefix
        // e.g. --mode=Block --filter "*MULMOD*" → --filter "*GasBlock*MULMOD*"
        string existing = remaining[filterIdx + 1].Trim('"');
        remaining[filterIdx + 1] = classFilter.TrimEnd('*') + "*" + existing.TrimStart('*');
    }
    else
    {
        // No existing filter — add one
        string[] withFilter = new string[remaining.Length + 2];
        Array.Copy(remaining, withFilter, remaining.Length);
        withFilter[remaining.Length] = "--filter";
        withFilter[remaining.Length + 1] = classFilter;
        remaining = withFilter;
    }

    return remaining;
}

static string[] ApplyChunkFilter(string[] args)
{
    int chunkIdx = -1;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--chunk", StringComparison.OrdinalIgnoreCase))
        {
            chunkIdx = i;
            break;
        }
    }

    if (chunkIdx < 0)
        return args;

    // Support --chunk N/M or --chunk=N/M
    string chunkValue;
    int removeCount;
    if (args[chunkIdx].Contains('=') || args[chunkIdx].Contains(':'))
    {
        int sep = args[chunkIdx].IndexOfAny(new[] { '=', ':' });
        chunkValue = args[chunkIdx].Substring(sep + 1);
        removeCount = 1;
    }
    else if (chunkIdx + 1 < args.Length)
    {
        chunkValue = args[chunkIdx + 1];
        removeCount = 2;
    }
    else
    {
        throw new ArgumentException("--chunk requires a value in format N/M (e.g. --chunk 2/5)");
    }

    string[] parts = chunkValue.Split('/');
    if (parts.Length != 2 || !int.TryParse(parts[0], out int n) || !int.TryParse(parts[1], out int m) || n < 1 || n > m)
    {
        throw new ArgumentException($"Invalid --chunk value: '{chunkValue}'. Expected format N/M where 1 <= N <= M (e.g. 2/5)");
    }

    GasBenchmarkConfig.ChunkIndex = n;
    GasBenchmarkConfig.ChunkTotal = m;

    // Remove --chunk from args
    string[] remaining = new string[args.Length - removeCount];
    Array.Copy(args, 0, remaining, 0, chunkIdx);
    Array.Copy(args, chunkIdx + removeCount, remaining, chunkIdx, args.Length - chunkIdx - removeCount);
    return remaining;
}

static void RunDiagnostic(string pattern)
{
    string repoRoot = FindRepoRoot();
    string gasBenchmarksRoot = Path.Combine(repoRoot, "tools", "gas-benchmarks");
    string genesisPath = Path.Combine(gasBenchmarksRoot, "scripts", "genesisfiles", "nethermind", "zkevmgenesis.json");
    string testingDir = Path.Combine(gasBenchmarksRoot, "eest_tests", "testing");

    // Find first test file matching the pattern
    string matchedFile = null;
    foreach (string dir in Directory.GetDirectories(testingDir))
    {
        foreach (string file in Directory.GetFiles(dir, $"*{pattern}*"))
        {
            matchedFile = file;
            break;
        }
        if (matchedFile is not null) break;
    }

    if (matchedFile is null)
    {
        Console.WriteLine($"ERROR: No test file matching '{pattern}' found");
        return;
    }

    Console.WriteLine($"Test file: {Path.GetFileName(matchedFile)}");

    IReleaseSpec pragueSpec = Prague.Instance;
    ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

    Console.WriteLine("Loading genesis...");
    Stopwatch sw = Stopwatch.StartNew();
    PayloadLoader.EnsureGenesisInitialized(genesisPath, pragueSpec);
    Console.WriteLine($"Genesis loaded in {sw.ElapsedMilliseconds}ms, StateRoot={PayloadLoader.GenesisStateRoot}");

    IWorldState state = PayloadLoader.CreateWorldState();
    BlockHeader genesisBlock = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
    {
        StateRoot = PayloadLoader.GenesisStateRoot
    };
    IDisposable scope = state.BeginScope(genesisBlock);

    // Load test payload
    (BlockHeader header, Transaction[] txs) = PayloadLoader.LoadPayload(matchedFile);
    Console.WriteLine($"Block: number={header.Number}, gasLimit={header.GasLimit}, gasUsed={header.GasUsed}");
    Console.WriteLine($"Transactions: {txs.Length}");

    for (int i = 0; i < txs.Length; i++)
    {
        Transaction tx = txs[i];
        Console.WriteLine($"\nTx[{i}]:");
        Console.WriteLine($"  SenderAddress: {tx.SenderAddress}");
        Console.WriteLine($"  To: {tx.To}");
        Console.WriteLine($"  Nonce: {tx.Nonce}");
        Console.WriteLine($"  GasLimit: {tx.GasLimit}");
        Console.WriteLine($"  GasPrice: {tx.GasPrice}");
        Console.WriteLine($"  Value: {tx.Value}");
        Console.WriteLine($"  Data length: {tx.Data.Length}");

        if (tx.SenderAddress is not null)
        {
            bool senderExists = state.AccountExists(tx.SenderAddress);
            Console.WriteLine($"  Sender exists: {senderExists}");
            if (senderExists)
            {
                Console.WriteLine($"  Sender balance: {state.GetBalance(tx.SenderAddress)}");
                Console.WriteLine($"  Sender nonce: {state.GetNonce(tx.SenderAddress)}");
            }
        }

        if (tx.To is not null)
        {
            bool toExists = state.AccountExists(tx.To);
            Console.WriteLine($"  To exists: {toExists}");
            if (toExists)
            {
                Console.WriteLine($"  To code size: {state.GetCodeHash(tx.To)}");
                Console.WriteLine($"  To has code: {state.GetCodeHash(tx.To) != Keccak.OfAnEmptyString}");
            }
        }
    }

    // Set up EVM and execute
    TestBlockhashProvider blockhashProvider = new();
    EthereumCodeInfoRepository codeInfoRepo = new(state);
    EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

    ITransactionProcessor txProcessor = new EthereumTransactionProcessor(
        BlobBaseFeeCalculator.Instance,
        specProvider,
        state,
        vm,
        codeInfoRepo,
        LimboLogs.Instance);

    // Execute setup payload if one exists (e.g. storage pre-population for SLOAD/SSTORE)
    string setupDir = Path.Combine(gasBenchmarksRoot, "eest_tests", "setup");
    string setupFile = FindSetupFile(setupDir, Path.GetFileName(matchedFile));
    if (setupFile is not null)
    {
        Console.WriteLine($"\nSetup file: {Path.GetFileName(setupFile)}");
        (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
        txProcessor.SetBlockExecutionContext(setupHeader);
        for (int i = 0; i < setupTxs.Length; i++)
        {
            txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
        }
        state.Commit(pragueSpec);
        Console.WriteLine($"Setup complete: {setupTxs.Length} transactions executed");
    }

    txProcessor.SetBlockExecutionContext(header);

    Console.WriteLine("\n--- Executing transactions ---");
    for (int i = 0; i < txs.Length; i++)
    {
        sw.Restart();
        TransactionResult result = txProcessor.BuildUp(txs[i], NullTxTracer.Instance);
        sw.Stop();
        Console.WriteLine($"Tx[{i}] result: {result}, elapsed: {sw.ElapsedMilliseconds}ms");
    }

    state.Reset();

    // Run once more with CallAndRestore
    Console.WriteLine("\n--- CallAndRestore ---");
    for (int i = 0; i < txs.Length; i++)
    {
        sw.Restart();
        txProcessor.CallAndRestore(txs[i], NullTxTracer.Instance);
        sw.Stop();
        Console.WriteLine($"Tx[{i}] CallAndRestore elapsed: {sw.ElapsedMilliseconds}ms");
    }

    scope.Dispose();
    Console.WriteLine("\nDiagnostic complete.");
}

static string FindSetupFile(string setupDir, string testFileName)
{
    if (!Directory.Exists(setupDir))
        return null;

    foreach (string dir in Directory.GetDirectories(setupDir))
    {
        string candidate = Path.Combine(dir, testFileName);
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

static string FindRepoRoot()
{
    string dir = AppDomain.CurrentDomain.BaseDirectory;
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir, ".git")))
            return dir;
        dir = Directory.GetParent(dir)?.FullName;
    }
    throw new DirectoryNotFoundException("Could not find repository root.");
}
