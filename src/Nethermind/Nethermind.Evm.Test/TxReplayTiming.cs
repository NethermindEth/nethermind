// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Timing harness, not a test: replays one mainnet transaction from a prestate-tracer dump
/// ({ "tx": eth_getTransactionByHash, "pre": prestateTracer, "header": number/timestamp/baseFeePerGas/gasLimit/miner })
/// and reports the time per execution. Set NETHERMIND_REPLAY_FILE (and optionally NETHERMIND_REPLAY_ROUNDS).
/// </summary>
[Explicit("timing harness")]
public class TxReplayTiming : VirtualMachineTestsBase
{
    private static readonly JsonElement Dump = Load();
    private static JsonElement Load()
    {
        string? path = Environment.GetEnvironmentVariable("NETHERMIND_REPLAY_FILE");
        return path is null ? default : JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static ulong Hex(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? ulong.Parse(v.GetString()![2..], NumberStyles.HexNumber) :
        e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt64() : 0;

    private static UInt256 Big(string hex) => UInt256.Parse("0" + (hex.StartsWith("0x") ? hex[2..] : hex), NumberStyles.HexNumber);

    protected override ulong BlockNumber => Dump.ValueKind == JsonValueKind.Undefined ? base.BlockNumber : Hex(Dump.GetProperty("header"), "number");
    protected override ulong Timestamp => Dump.ValueKind == JsonValueKind.Undefined ? base.Timestamp : Hex(Dump.GetProperty("header"), "timestamp");

    [Test]
    public void Replay()
    {
        Assert.That(Dump.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined), "set NETHERMIND_REPLAY_FILE");
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_REPLAY_ROUNDS"), out int r) ? r : 200;

        foreach (JsonProperty account in Dump.GetProperty("pre").EnumerateObject())
        {
            Address address = new(account.Name);
            JsonElement a = account.Value;
            UInt256 balance = a.TryGetProperty("balance", out JsonElement b) ? Big(b.GetString()!) : UInt256.Zero;
            ulong nonce = a.TryGetProperty("nonce", out JsonElement n) ? n.GetUInt64() : 0;
            TestState.CreateAccount(address, balance, nonce);
            if (a.TryGetProperty("code", out JsonElement code) && code.GetString() is { Length: > 2 } codeHex)
                TestState.InsertCode(address, Bytes.FromHexString(codeHex), Spec);
            if (a.TryGetProperty("storage", out JsonElement storage))
            {
                foreach (JsonProperty slot in storage.EnumerateObject())
                    TestState.Set(new StorageCell(address, Big(slot.Name)), new UInt256(Bytes.FromHexString(slot.Value.GetString()!), isBigEndian: true));
            }
        }

        TestState.Commit(Spec);
        TestState.CommitTree(0);

        JsonElement t = Dump.GetProperty("tx");
        JsonElement h = Dump.GetProperty("header");
        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithTo(new Address(t.GetProperty("to").GetString()!))
            .WithData(Bytes.FromHexString(t.GetProperty("input").GetString()!))
            .WithGasLimit(Hex(t, "gas"))
            .WithMaxFeePerGas(Big(t.GetProperty("maxFeePerGas").GetString()!))
            .WithMaxPriorityFeePerGas(Big(t.GetProperty("maxPriorityFeePerGas").GetString()!))
            .WithNonce(Hex(t, "nonce"))
            .WithValue(Big(t.GetProperty("value").GetString()!))
            .WithSenderAddress(new Address(t.GetProperty("from").GetString()!))
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(Hex(h, "number"))
            .WithTimestamp(Hex(h, "timestamp"))
            .WithBaseFeePerGas(Big(h.GetProperty("baseFeePerGas").GetString()!))
            .WithGasLimit(Hex(h, "gasLimit"))
            .WithBeneficiary(new Address(h.GetProperty("miner").GetString()!))
            .WithTransactions(tx)
            .TestObject;

        BlockExecutionContext context = new(block.Header, Spec);
        TransactionResult first = _processor.CallAndRestore(tx, in context, NullTxTracer.Instance);
        for (int i = 0; i < 20; i++) _processor.CallAndRestore(tx, in context, NullTxTracer.Instance);

        List<double> times = [];
        for (int i = 0; i < rounds; i++)
        {
            long start = Stopwatch.GetTimestamp();
            _processor.CallAndRestore(tx, in context, NullTxTracer.Instance);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        times.Sort();
        string line = $"{Path.GetFileName(Environment.GetEnvironmentVariable("NETHERMIND_REPLAY_FILE"))} result {first} rounds {rounds}: median {times[times.Count / 2]:F1} us, min {times[0]:F1} us, p90 {times[times.Count * 9 / 10]:F1} us";
        File.AppendAllText("C:/tmp/replay-timing.txt", line + Environment.NewLine);
        TestContext.Out.WriteLine(line);
    }
}
