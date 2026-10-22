// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Evm.Test;

[TestFixture]
public class T8nExecutorTests
{
    private static readonly UInt256 AllocatedBalance = 1.Ether;

    private string _inputDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _inputDirectory = Path.Combine(Path.GetTempPath(), $"t8n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_inputDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_inputDirectory, recursive: true);

    [Test]
    public void Execute_applies_non_empty_alloc()
    {
        T8nCommandArguments arguments = WriteInputs();

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        Assert.That(result.PostState.StateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        Assert.That(result.Accounts.TryGetValue(TestItem.AddressA, out AccountState? account), Is.True);
        Assert.That(account!.Balance, Is.EqualTo(AllocatedBalance));
    }

    [Test]
    public void Execute_records_parent_hash_in_history_contract()
    {
        Address historyContract = Eip2935Constants.BlockHashHistoryAddress;
        T8nCommandArguments arguments = WriteInputs(
            fork: "Prague",
            alloc: $$"""
                {
                    "{{historyContract}}": { "balance": "0x0", "code": "0x00", "nonce": "0x1" },
                    "{{Eip7002Constants.WithdrawalRequestPredeployAddress}}": { "balance": "0x0", "code": "0x00", "nonce": "0x1" },
                    "{{Eip7251Constants.ConsolidationRequestPredeployAddress}}": { "balance": "0x0", "code": "0x00", "nonce": "0x1" }
                }
                """,
            extraEnv: $$""", "parentBeaconBlockRoot": "{{Keccak.Zero}}", "blockHashes": { "0x0": "{{TestItem.KeccakA}}" }""");

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        Assert.That(result.Accounts[historyContract].Storage[UInt256.Zero], Is.EqualTo(TestItem.KeccakA.BytesToArray()));
    }

    [Test]
    public void Execute_rejects_transactions_at_their_input_index()
    {
        T8nCommandArguments arguments = WriteInputs(txs: $"[{SignedTx(TestItem.AddressB, nonce: 0, gas: "0x1")}, {SignedTx(TestItem.AddressB, nonce: 1)}]");

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        Assert.That(result.PostState.Rejected!.Select(rejected => rejected.Index), Is.EqualTo([0, 1]));
    }

    [Test]
    public void Execute_names_trace_files_by_included_transaction_count()
    {
        T8nCommandArguments arguments = WriteInputs(txs: $"[{SignedTx(TestItem.AddressB, nonce: 1)}, {SignedTx(TestItem.AddressB, nonce: 0)}]");
        arguments.Trace = true;
        arguments.OutputBaseDir = Path.Combine(_inputDirectory, "traces");

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        string[] traceFiles = [.. Directory.GetFiles(arguments.OutputBaseDir, "trace-*.jsonl").Select(path => Path.GetFileName(path))];
        Assert.That(traceFiles, Has.Length.EqualTo(2).And.All.StartWith("trace-0-"));
        Assert.That(traceFiles, Does.Contain($"trace-0-{result.PostState.Receipts!.Single().TransactionHash}.jsonl"));
    }

    [TestCase("-1", false)]
    [TestCase("0x80", true)]
    public void Execute_applies_block_reward_unless_disabled(string reward, bool coinbaseRewarded)
    {
        T8nCommandArguments arguments = WriteInputs(fork: "Byzantium", reward: reward);

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        Assert.That(result.Accounts.ContainsKey(TestItem.AddressB), Is.EqualTo(coinbaseRewarded));
    }

    [Test]
    public void Execute_reports_logs_in_geth_layout()
    {
        const string log0 = "0x60006000a0";
        T8nCommandArguments arguments = WriteInputs(
            alloc: $$"""
                {
                    "{{TestItem.AddressA}}": { "balance": "{{AllocatedBalance}}" },
                    "{{TestItem.AddressC}}": { "balance": "0x0", "code": "{{log0}}" }
                }
                """,
            txs: $"[{SignedTx(TestItem.AddressC, nonce: 0)}]");

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        T8nReceipt receipt = result.PostState.Receipts!.Single();
        T8nLog log = receipt.Logs.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(log.Address, Is.EqualTo(TestItem.AddressC));
            Assert.That(log.TransactionHash, Is.EqualTo(receipt.TransactionHash));
            Assert.That(log.BlockHash, Is.EqualTo(receipt.BlockHash));
            Assert.That(result.PostState.LogsHash, Is.EqualTo(Keccak.Compute(Rlp.Encode([new LogEntry(TestItem.AddressC, [], [])]).Bytes)));
        }
    }

    private static string SignedTx(Address to, ulong nonce, string gas = "0x10000") => $$"""
        {
            "nonce": "{{nonce.ToHexString(skipLeadingZeros: true)}}",
            "gasPrice": "0x0",
            "gas": "{{gas}}",
            "to": "{{to}}",
            "value": "0x0",
            "input": "0x",
            "v": "0x0",
            "r": "0x0",
            "s": "0x0",
            "secretKey": "{{TestItem.PrivateKeyA.KeyBytes.ToHexString(true)}}"
        }
        """;

    private T8nCommandArguments WriteInputs(string fork = "Shanghai", string? alloc = null, string txs = "[]", string extraEnv = "", string reward = "0")
    {
        Write("alloc.json", alloc ?? $$"""
            {
                "{{TestItem.AddressA}}": { "balance": "{{AllocatedBalance}}" }
            }
            """);
        Write("env.json", $$"""
            {
                "currentCoinbase": "{{TestItem.AddressB}}",
                "currentGasLimit": "0x1c9c380",
                "currentNumber": "0x1",
                "currentTimestamp": "0x0c",
                "currentDifficulty": "0x0",
                "currentBaseFee": "0x0",
                "currentRandom": "0x0",
                "withdrawals": []{{extraEnv}}
            }
            """);
        Write("txs.json", txs);

        return new T8nCommandArguments
        {
            InputAlloc = Path.Combine(_inputDirectory, "alloc.json"),
            InputEnv = Path.Combine(_inputDirectory, "env.json"),
            InputTxs = Path.Combine(_inputDirectory, "txs.json"),
            StateFork = fork,
            StateChainId = 1,
            StateReward = reward,
        };
    }

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_inputDirectory, fileName), content);
}
