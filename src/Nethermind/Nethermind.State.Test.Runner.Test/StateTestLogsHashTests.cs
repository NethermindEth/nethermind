// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json;
using Nethermind.Test.Runner;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// A state test's <c>logs</c> is keccak256 of the RLP list of the logs its transaction leaves, and the runner
/// must check it next to the post-state root.
/// </summary>
public class StateTestLogsHashTests
{
    /// <summary>The logs hash of <c>stLogTests/log1_nonEmptyMem</c> as testeth filled it: one LOG1 from a nested call.</summary>
    private const string Log1LogsHash = "0x2e3c489a64cf3233b1ac4d42fd1f6e2430f6d99524c57dba5471d3b41a20fdc0";

    private static readonly string EmptyLogsHash = Keccak.OfAnEmptySequenceRlp.ToString();

    public enum NoLogsScenario
    {
        Transfer,
        LogThenRevert,
        GasLimitBelowIntrinsicGas,
        InsufficientBalance,
    }

    [Test]
    public async Task Logs_hash_is_checked_against_the_fixture(
        [Values("Frontier", "Byzantium")] string fork,
        [Values] bool fixtureMatches,
        [Values] bool traced)
    {
        string expectedLogs = fixtureMatches ? Log1LogsHash : EmptyLogsHash;

        EthereumTestResult result = await Run(Log1NonEmptyMem(fork, expectedLogs), traced);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.EqualTo(fixtureMatches), result.Error);
            Assert.That(result.Error, fixtureMatches ? Is.Null : Is.EqualTo($"LOGS HASH exp: {expectedLogs}, actual: {Log1LogsHash}"));
        }
    }

    [Test]
    public async Task Transaction_that_leaves_no_logs_has_the_empty_list_hash([Values] NoLogsScenario scenario, [Values] bool fixtureExpectsNoLogs)
    {
        // The post-state root is not under test here, so it comes from a run that the zero placeholder keeps to the root alone.
        EthereumTestResult rootOnly = await Run(NoLogs(scenario, Keccak.Zero.ToString(), Keccak.Zero.ToString()), traced: false);
        string expectedLogs = fixtureExpectsNoLogs ? EmptyLogsHash : Log1LogsHash;

        EthereumTestResult result = await Run(NoLogs(scenario, rootOnly.StateRoot!.ToString(), expectedLogs), traced: false);

        bool rejected = scenario is NoLogsScenario.GasLimitBelowIntrinsicGas or NoLogsScenario.InsufficientBalance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Pass, Is.EqualTo(fixtureExpectsNoLogs), result.Error);
            Assert.That(result.Error, fixtureExpectsNoLogs
                ? rejected ? Is.Not.Null : Is.Null
                : Is.EqualTo($"LOGS HASH exp: {Log1LogsHash}, actual: {EmptyLogsHash}"));
        }
    }

    [Test]
    public async Task Fixture_without_a_logs_hash_is_checked_on_its_state_root_only([Values] bool zeroPlaceholder)
    {
        EthereumTestResult result = await Run(Log1NonEmptyMem("Byzantium", zeroPlaceholder ? Keccak.Zero.ToString() : null), traced: false);

        Assert.That(result.Pass, Is.True, result.Error);
    }

    [Test]
    public async Task Logs_check_leaves_the_trace_of_the_callers_tracer_unchanged()
    {
        using StateTestTxTracer rootOnlyTracer = new(standardIntrinsicGas: 0, destroyRefund: 0);
        using StateTestTxTracer logsCheckedTracer = new(standardIntrinsicGas: 0, destroyRefund: 0);

        EthereumTestResult rootOnly = await new StateTestExecutor().Execute(Load(Log1NonEmptyMem("Byzantium", null)), rootOnlyTracer);
        EthereumTestResult logsChecked = await new StateTestExecutor().Execute(Load(Log1NonEmptyMem("Byzantium", Log1LogsHash)), logsCheckedTracer);

        EthereumJsonSerializer serializer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootOnly.Pass, Is.True, rootOnly.Error);
            Assert.That(logsChecked.Pass, Is.True, logsChecked.Error);
            Assert.That(logsCheckedTracer.BuildResult().Entries, Is.Not.Empty);
            Assert.That(serializer.Serialize(logsCheckedTracer.BuildResult()), Is.EqualTo(serializer.Serialize(rootOnlyTracer.BuildResult())));
        }
    }

    private static async Task<EthereumTestResult> Run(string json, bool traced)
    {
        GeneralStateTest test = Load(json);
        if (!traced)
        {
            StateTestsRunner runner = new(WhenTrace.Never, traceMemory: false, traceStack: false, chainId: BlockchainIds.Mainnet, suppressOutput: true);
            return await runner.RunSingleTestAsync(test);
        }

        using StateTestTxTracer tracer = new(standardIntrinsicGas: 0, destroyRefund: 0);
        return await new StateTestExecutor().Execute(test, tracer);
    }

    private static GeneralStateTest Load(string json)
    {
        GeneralStateTest test = JsonToEthereumTest.ConvertStateTest(json).Single();
        Assert.That(test.LoadFailure, Is.Null);
        return test;
    }

    /// <summary>
    /// <c>stLogTests/log1_nonEmptyMem</c> from LegacyTests/Constantinople with the post-state roots testeth filled for
    /// <paramref name="fork"/>: Frontier precedes EIP-658, so its receipts carry a state root, and Byzantium follows it.
    /// </summary>
    /// <param name="logs">The fixture's <c>logs</c>, or <see langword="null"/> to leave the field out.</param>
    private static string Log1NonEmptyMem(string fork, string logs)
    {
        string hash = fork == "Frontier"
            ? "0xb67c50fb99df15cef50a230f584742d3cfc79da986a70aaf070be16716794682"
            : "0x8d085a84675a7c1faa1ba717f1b3538efb88847d2d59c4d0e18cacb6c82b19bc";
        string logsField = logs is null ? "" : $"\"logs\": \"{logs}\",";

        return $$"""
            {
              "log1_nonEmptyMem": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x020000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "0x5e20a0453cecd065ea59c37ac63e079ee08998b6045136a8ce6635c7912ec0b6"
                },
                "pre": {
                  "0x095e7baea6a6c7c4c2dfeb977efac326af552d87": {
                    "balance": "0x0de0b6b3a7640000", "nonce": "0x00", "storage": {},
                    "code": "0x60006000600060006017730f572e5295c57f15886f9b263e2f6d2d6c7b5ec66103e8f1600055"
                  },
                  "0x0f572e5295c57f15886f9b263e2f6d2d6c7b5ec6": {
                    "balance": "0x0de0b6b3a7640000", "nonce": "0x00", "storage": {},
                    "code": "0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff600052600060206000a1"
                  },
                  "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b": { "balance": "0x0de0b6b3a7640000", "code": "0x", "nonce": "0x00", "storage": {} }
                },
                "transaction": {
                  "data": ["0x"],
                  "gasLimit": ["0x033450"],
                  "gasPrice": "0x01",
                  "nonce": "0x00",
                  "secretKey": "0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8",
                  "to": "0x095e7baea6a6c7c4c2dfeb977efac326af552d87",
                  "value": ["0x0186a0"]
                },
                "post": {
                  "{{fork}}": [{ "hash": "{{hash}}", {{logsField}} "indexes": { "data": 0, "gas": 0, "value": 0 } }]
                }
              }
            }
            """;
    }

    /// <summary>A Byzantium transaction to a contract running <paramref name="scenario"/>'s code, or rejected before it runs.</summary>
    private static string NoLogs(NoLogsScenario scenario, string hash, string logs)
    {
        (string code, string gasLimit, string value) = scenario switch
        {
            NoLogsScenario.Transfer => ("0x", "0x5208", "0x01"),
            // LOG0 then REVERT: the reverted frame takes its log with it.
            NoLogsScenario.LogThenRevert => ("0x60006000a060006000fd", "0x0186a0", "0x00"),
            NoLogsScenario.GasLimitBelowIntrinsicGas => ("0x", "0x5207", "0x01"),
            NoLogsScenario.InsufficientBalance => ("0x", "0x5208", "0x0de0b6b3a7640000"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        return $$"""
            {
              "no_logs": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x020000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "0x0000000000000000000000000000000000000000000000000000000000000000"
                },
                "pre": {
                  "0x00000000000000000000000000000000000000aa": { "balance": "0x00", "code": "{{code}}", "nonce": "0x00", "storage": {} },
                  "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b": { "balance": "0x0de0b6b3a7640000", "code": "0x", "nonce": "0x00", "storage": {} }
                },
                "transaction": {
                  "data": ["0x"],
                  "gasLimit": ["{{gasLimit}}"],
                  "gasPrice": "0x0a",
                  "nonce": "0x00",
                  "secretKey": "0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8",
                  "to": "0x00000000000000000000000000000000000000aa",
                  "value": ["{{value}}"]
                },
                "post": {
                  "Byzantium": [{ "hash": "{{hash}}", "logs": "{{logs}}", "indexes": { "data": 0, "gas": 0, "value": 0 } }]
                }
              }
            }
            """;
    }

    private sealed class StateTestExecutor : GeneralStateTestBase
    {
        public Task<EthereumTestResult> Execute(GeneralStateTest test, ITxTracer txTracer) => RunTest(test, txTracer);
    }
}
