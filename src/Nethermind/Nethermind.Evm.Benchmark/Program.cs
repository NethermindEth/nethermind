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

BenchmarkSwitcher.FromAssembly(typeof(Nethermind.Evm.Benchmark.EvmBenchmarks).Assembly).Run(args);

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
