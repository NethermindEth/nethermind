// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;
using Testably.Abstractions.Testing;

namespace Nethermind.Evm.Test.Tracing;

/// <summary>
/// Geth's error text for a creation whose code deposit fails, in the Geth tracers that report frame errors beside
/// instructions.
/// </summary>
public class GethLikeCodeDepositErrorTests : VirtualMachineTestsBase
{
    private const string RootError = "invalid jump destination";

    /// <remarks>
    /// The oversized deposit has the gas to pay for itself, as Geth checks the size after charging the deposit before
    /// Amsterdam, and before charging it from Amsterdam on.
    /// </remarks>
    public enum Deposit { StorageOutOfGas, Oversize, InvalidCode }

    public enum Frame { Root, Child, ChildThenRootFails }

    [Test]
    public void File_trace_exits_report_geth_error([Values] Deposit deposit, [Values] Frame frame)
    {
        (Block block, Transaction transaction) = PrepareCreation(deposit, frame);
        MockFileSystem fileSystem = new();
        fileSystem.Initialize();
        using GethLikeBlockFileTracer tracer = new(block, GethTraceOptions.Default, fileSystem, SpecProvider.GetSpec(block.Header));

        Trace(tracer, block, transaction);

        // Exit records and the closing summary, which carries the root's error, are the records with an output.
        string?[] errors = fileSystem.File.ReadAllLines(tracer.FileNames.Single())
            .Where(static line => line.StartsWith("{\"output\":", StringComparison.Ordinal))
            .Select(ErrorOf)
            .ToArray();
        Assert.That(errors, Is.EqualTo(ExpectedErrors(deposit, frame)));
    }

    [Test]
    public void JavaScript_tracer_reports_geth_error([Values] Deposit deposit, [Values] Frame frame)
    {
        const string userTracer = """
            {
                errors: [],
                enter: function(frame) { },
                exit: function(frame) { this.errors.push(frame.getError() || null); },
                fault: function(log) { },
                result: function(ctx) { return this.errors.concat([ctx.error || null]); }
            }
            """;
        (Block block, Transaction transaction) = PrepareCreation(deposit, frame);
        using GethLikeBlockJavaScriptTracer tracer = new(TestState, SpecProvider.GetSpec(block.Header), GethTraceOptions.Default with { Tracer = userTracer });

        Trace(tracer, block, transaction);

        using GethLikeTxTrace trace = tracer.BuildResult().Single();
        string?[]? errors = JsonSerializer.Deserialize<string?[]>(JsonSerializer.Serialize(trace.CustomTracerResult, EthereumJsonSerializer.JsonOptions));
        Assert.That(errors, Is.EqualTo(ExpectedErrors(deposit, frame)));
    }

    /// <summary>The child's error, if a child creates, then the root's.</summary>
    private static string?[] ExpectedErrors(Deposit deposit, Frame frame)
    {
        string error = deposit switch
        {
            Deposit.StorageOutOfGas => "contract creation code storage out of gas",
            Deposit.InvalidCode => "invalid code: must not begin with 0xef",
            _ => "max code size exceeded: code size 24577 limit 24576"
        };
        return frame switch
        {
            Frame.Root => [error],
            Frame.Child => [error, null],
            _ => [error, RootError]
        };
    }

    private (Block block, Transaction transaction) PrepareCreation(Deposit deposit, Frame frame)
    {
        ForkActivation activation = MainnetSpecProvider.CancunActivation;
        int length = deposit switch
        {
            // 1024 bytes need 204,800 deposit gas, more than the 100,000 the transaction has.
            Deposit.StorageOutOfGas => 1024,
            Deposit.InvalidCode => 1,
            _ => checked((int)MainnetSpecProvider.Instance.GetSpec(activation).MaxCodeSize + 1)
        };
        // The oversized deposit needs about 4.9M gas.
        ulong gasLimit = deposit == Deposit.Oversize ? 6_000_000UL : 100_000UL;
        byte firstByte = deposit == Deposit.InvalidCode ? (byte)0xEF : (byte)0;
        byte[] initCode = Prepare.EvmCode.PushData(firstByte).PushData(0).Op(Instruction.MSTORE8)
            .PushData(length).PushData(0).Op(Instruction.RETURN).Done;
        if (frame == Frame.Root)
            return PrepareInitTx(activation, gasLimit, initCode);

        Prepare code = Prepare.EvmCode.Create(initCode, 0);
        // Offset 0 holds no JUMPDEST, so the jump fails the root after the failed creation.
        code = frame == Frame.Child ? code.STOP() : code.PushData(0).Op(Instruction.JUMP);
        return PrepareTx(activation, gasLimit, code.Done);
    }

    private void Trace(IBlockTracer tracer, Block block, Transaction transaction)
    {
        tracer.StartNewBlockTrace(block);
        ITxTracer txTracer = tracer.StartNewTxTrace(transaction);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), txTracer);
        tracer.EndTxTrace();
        tracer.EndBlockTrace();
    }

    private static string? ErrorOf(string record)
    {
        using JsonDocument document = JsonDocument.Parse(record);
        return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() : null;
    }
}
