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
using Nethermind.Evm.Benchmark;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

if (args.Length > 0 && args[0] == "--diag")
{
    string pattern = args.Length > 1 ? args[1] : "*";
    RunDiagnostic(pattern);
    return;
}

int inprocessIndex = Array.IndexOf(args, "--inprocess");
if (inprocessIndex >= 0)
{
    GasBenchmarkConfig.InProcess = true;
    args = RemoveArguments(args, inprocessIndex, 1);
}

args = ApplyModeFilter(args);
args = ApplyChunkFilter(args);

ConfigureTimingFilePath();
BenchmarkSwitcher.FromAssembly(typeof(EvmBenchmarks).Assembly).Run(args);
GasNewPayloadMeasuredBenchmarks.PrintFinalTimingBreakdown();
GasNewPayloadBenchmarks.PrintFinalTimingBreakdown();

static void ConfigureTimingFilePath()
{
    string measuredTimingFilePath = Path.Combine(
        Path.GetTempPath(),
        $"nethermind-newpayload-timing-{Guid.NewGuid():N}.jsonl");
    string measuredTimingReportFilePath = Path.Combine(
        Directory.GetCurrentDirectory(),
        "BenchmarkDotNet.Artifacts",
        "results",
        $"newpayload-measured-timing-breakdown-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
    string realTimingFilePath = Path.Combine(
        Path.GetTempPath(),
        $"nethermind-newpayload-real-timing-{Guid.NewGuid():N}.jsonl");
    string realTimingReportFilePath = Path.Combine(
        Directory.GetCurrentDirectory(),
        "BenchmarkDotNet.Artifacts",
        "results",
        $"newpayload-timing-breakdown-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");

    Environment.SetEnvironmentVariable(GasNewPayloadMeasuredBenchmarks.TimingFileEnvVar, measuredTimingFilePath);
    Environment.SetEnvironmentVariable(GasNewPayloadMeasuredBenchmarks.TimingReportFileEnvVar, measuredTimingReportFilePath);
    Environment.SetEnvironmentVariable(GasNewPayloadBenchmarks.TimingFileEnvVar, realTimingFilePath);
    Environment.SetEnvironmentVariable(GasNewPayloadBenchmarks.TimingReportFileEnvVar, realTimingReportFilePath);
}

static string[] RemoveArguments(string[] args, int index, int removeCount)
{
    string[] remaining = new string[args.Length - removeCount];
    Array.Copy(args, 0, remaining, 0, index);
    Array.Copy(args, index + removeCount, remaining, index, args.Length - index - removeCount);
    return remaining;
}

static string[] MergeWithClassFilter(string[] args, string classFilter)
{
    int filterIndex = Array.IndexOf(args, "--filter");
    if (filterIndex >= 0 && filterIndex + 1 < args.Length)
    {
        string existingFilter = args[filterIndex + 1].Trim('"');
        args[filterIndex + 1] = classFilter.TrimEnd('*') + "*" + existingFilter.TrimStart('*');
        return args;
    }

    string[] withFilter = new string[args.Length + 2];
    Array.Copy(args, withFilter, args.Length);
    withFilter[args.Length] = "--filter";
    withFilter[args.Length + 1] = classFilter;
    return withFilter;
}

static (string Value, int RemoveCount) GetOptionValue(string[] args, int optionIndex, string optionName)
{
    string token = args[optionIndex];
    int separatorIndex = token.IndexOfAny(new[] { '=', ':' });
    if (separatorIndex >= 0)
    {
        return (token[(separatorIndex + 1)..], 1);
    }

    if (optionIndex + 1 < args.Length)
    {
        return (args[optionIndex + 1], 2);
    }

    throw new ArgumentException($"{optionName} requires a value.");
}

static (string ClassFilter, bool? BuildBlocksOnMainState) ResolveModeDefinition(string modeValue)
{
    switch (modeValue.ToUpperInvariant())
    {
        case "EVMEXECUTE":
            return ("*GasPayloadExecuteBenchmarks*", null);
        case "EVMBUILDUP":
            return ("*GasPayloadBenchmarks*", null);
        case "BLOCKBUILDING":
            return ("*GasBlockBuildingBenchmarks*", false);
        case "BLOCKBUILDINGMAINSTATE":
            return ("*GasBlockBuildingBenchmarks*", true);
        case "BLOCKONE":
            return ("*GasBlockOne*", null);
        case "BLOCK":
            return ("*GasBlockBenchmarks*", null);
        case "NEWPAYLOAD":
            return ("*GasNewPayloadBenchmarks*", null);
        case "NEWPAYLOADMEASURED":
            return ("*GasNewPayloadMeasuredBenchmarks*", null);
        default:
            throw new ArgumentException($"Unknown --mode value: '{modeValue}'. Expected 'EVMExecute', 'EVMBuildUp', 'BlockBuilding', 'BlockBuildingMainState', 'BlockOne', 'Block', 'NewPayload', or 'NewPayloadMeasured'.");
    }
}

static string[] ApplyModeFilter(string[] args)
{
    int modeIndex = -1;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--mode=", StringComparison.OrdinalIgnoreCase)
            || args[i].StartsWith("--mode:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase))
        {
            modeIndex = i;
            break;
        }
    }

    if (modeIndex < 0)
    {
        return args;
    }

    (string modeValue, int removeCount) = GetOptionValue(args, modeIndex, "--mode");
    (string classFilter, bool? buildBlocksOnMainStateValue) = ResolveModeDefinition(modeValue);
    if (buildBlocksOnMainStateValue is bool buildBlocksOnMainState)
    {
        Environment.SetEnvironmentVariable(
            GasBlockBuildingBenchmarks.BuildBlocksOnMainStateEnvVar,
            buildBlocksOnMainState ? bool.TrueString : bool.FalseString);
    }

    string[] remaining = RemoveArguments(args, modeIndex, removeCount);
    return MergeWithClassFilter(remaining, classFilter);
}

static string[] ApplyChunkFilter(string[] args)
{
    int chunkIndex = -1;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--chunk", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[i], "--chunk", StringComparison.OrdinalIgnoreCase))
        {
            chunkIndex = i;
            break;
        }
    }

    if (chunkIndex < 0)
    {
        return args;
    }

    (string chunkValue, int removeCount) = GetOptionValue(args, chunkIndex, "--chunk");
    string[] parts = chunkValue.Split('/');
    if (parts.Length != 2 || !int.TryParse(parts[0], out int n) || !int.TryParse(parts[1], out int m) || n < 1 || n > m)
    {
        throw new ArgumentException($"Invalid --chunk value: '{chunkValue}'. Expected format N/M where 1 <= N <= M (e.g. 2/5)");
    }

    GasBenchmarkConfig.ChunkIndex = n;
    GasBenchmarkConfig.ChunkTotal = m;
    return RemoveArguments(args, chunkIndex, removeCount);
}

static void RunDiagnostic(string pattern)
{
    string repoRoot = FindRepoRoot();
    string gasBenchmarksRoot = Path.Combine(repoRoot, "tools", "gas-benchmarks");
    string genesisPath = Path.Combine(gasBenchmarksRoot, "scripts", "genesisfiles", "nethermind", "zkevmgenesis.json");
    string testingDir = Path.Combine(gasBenchmarksRoot, "eest_tests", "testing");

    string matchedFile = FindFirstMatchingTestFile(testingDir, pattern);
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

    string setupFile = GasPayloadBenchmarks.FindSetupFile(Path.GetFileName(matchedFile));
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

static string FindFirstMatchingTestFile(string testingDir, string pattern)
{
    foreach (string dir in Directory.GetDirectories(testingDir))
    {
        foreach (string file in Directory.GetFiles(dir, $"*{pattern}*"))
        {
            return file;
        }
    }

    return null;
}

static string FindRepoRoot()
{
    string dir = AppDomain.CurrentDomain.BaseDirectory;
    while (dir is not null)
    {
        string gitPath = Path.Combine(dir, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
        {
            return dir;
        }

        dir = Directory.GetParent(dir)?.FullName;
    }

    throw new DirectoryNotFoundException("Could not find repository root.");
}
