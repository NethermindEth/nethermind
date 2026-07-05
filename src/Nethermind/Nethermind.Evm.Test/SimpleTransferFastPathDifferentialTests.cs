// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[NonParallelizable]
public class SimpleTransferFastPathDifferentialTests
{
    public enum RecipientKind
    {
        Existing,
        ExistingEmpty,
        Nonexistent,
        Self,
        Delegated,
        Precompile
    }

    public enum Mode
    {
        Execute,
        CallAndRestore,
        BuildUp
    }

    public enum TracerShape
    {
        None,
        AllOn,
        ActionsNoState
    }

    private sealed record ExecutionCapture(
        bool Executed,
        byte Status,
        string? Error,
        ulong GasSpent,
        ulong EffectiveBlockGas,
        ulong HeaderGasUsed,
        Hash256? StateRoot,
        UInt256 SenderBalance,
        UInt256 RecipientBalance,
        long FastPathEngaged,
        string[] TracerEvents);

    private static IEnumerable<TestCaseData> Scenarios()
    {
        RecipientKind[] valueTransferKinds = [RecipientKind.Existing, RecipientKind.ExistingEmpty, RecipientKind.Nonexistent, RecipientKind.Self, RecipientKind.Delegated];
        foreach (RecipientKind kind in valueTransferKinds)
        {
            foreach (ulong value in (ulong[])[1_000_000, 0])
            {
                foreach (bool eip7708 in (bool[])[true, false])
                {
                    yield return Case(value, kind, eip7708, dataLength: 0, TracerShape.AllOn, Mode.Execute);
                }
            }
        }

        yield return Case(1_000_000, RecipientKind.Existing, eip7708: true, dataLength: 300, TracerShape.AllOn, Mode.Execute);
        yield return Case(0, RecipientKind.Nonexistent, eip7708: false, dataLength: 300, TracerShape.AllOn, Mode.Execute);

        yield return Case(1_000_000, RecipientKind.Existing, eip7708: true, dataLength: 0, TracerShape.ActionsNoState, Mode.CallAndRestore);
        yield return Case(1_000_000, RecipientKind.Nonexistent, eip7708: false, dataLength: 0, TracerShape.ActionsNoState, Mode.BuildUp);
        yield return Case(1_000_000, RecipientKind.Self, eip7708: true, dataLength: 0, TracerShape.ActionsNoState, Mode.CallAndRestore);

        yield return Case(1_000_000, RecipientKind.Existing, eip7708: true, dataLength: 0, TracerShape.ActionsNoState, Mode.Execute);
        yield return Case(1_000_000, RecipientKind.Nonexistent, eip7708: false, dataLength: 0, TracerShape.None, Mode.Execute);
        yield return Case(0, RecipientKind.Self, eip7708: false, dataLength: 300, TracerShape.None, Mode.Execute);

        yield return Case(1_000_000, RecipientKind.Precompile, eip7708: false, dataLength: 0, TracerShape.AllOn, Mode.Execute);
        yield return Case(0, RecipientKind.Precompile, eip7708: false, dataLength: 300, TracerShape.AllOn, Mode.Execute);
    }

    private static TestCaseData Case(ulong value, RecipientKind kind, bool eip7708, int dataLength, TracerShape tracer, Mode mode) =>
        new TestCaseData(value, kind, eip7708, dataLength, tracer, mode)
            .SetName($"{mode}_{kind}_value{value}_7708{(eip7708 ? "On" : "Off")}_data{dataLength}_{tracer}");

    [TestCaseSource(nameof(Scenarios))]
    public void Fast_path_and_evm_path_are_equivalent(ulong value, RecipientKind recipientKind, bool eip7708, int dataLength, TracerShape tracer, Mode mode)
    {
        ExecutionCapture fast = Run(forceEvmPath: false, value, recipientKind, eip7708, dataLength, tracer, mode);
        ExecutionCapture slow = Run(forceEvmPath: true, value, recipientKind, eip7708, dataLength, tracer, mode);

        bool fastPathExpectedToEngage = recipientKind is not (RecipientKind.Delegated or RecipientKind.Precompile);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fast.Executed, Is.EqualTo(slow.Executed), "executed");
            Assert.That(fast.Status, Is.EqualTo(slow.Status), "status code");
            Assert.That(fast.Error, Is.EqualTo(slow.Error), "error");
            Assert.That(fast.GasSpent, Is.EqualTo(slow.GasSpent), "spent gas");
            Assert.That(fast.EffectiveBlockGas, Is.EqualTo(slow.EffectiveBlockGas), "effective block gas");
            Assert.That(fast.HeaderGasUsed, Is.EqualTo(slow.HeaderGasUsed), "header GasUsed");
            Assert.That(fast.SenderBalance, Is.EqualTo(slow.SenderBalance), "sender balance");
            Assert.That(fast.RecipientBalance, Is.EqualTo(slow.RecipientBalance), "recipient balance");
            Assert.That(fast.StateRoot, Is.EqualTo(slow.StateRoot), "state root");
            Assert.That(WithoutAccountReads(fast.TracerEvents), Is.EqualTo(WithoutAccountReads(slow.TracerEvents)), "tracer event sequence");
            Assert.That(StateReportedAddresses(fast.TracerEvents), Is.EquivalentTo(StateReportedAddresses(slow.TracerEvents)), "state-reported address set");
            Assert.That(fast.FastPathEngaged, Is.EqualTo(fastPathExpectedToEngage ? 1 : 0), "fast path engaged");
            Assert.That(slow.FastPathEngaged, Is.Zero, "forced EVM path did not engage the fast path");
        }
    }

    private static string[] WithoutAccountReads(string[] events) =>
        events.Where(static e => !e.StartsWith("AccountRead(", StringComparison.Ordinal)).ToArray();

    private static string[] StateReportedAddresses(string[] events) =>
        events
            .Where(static e => e.StartsWith("AccountRead(", StringComparison.Ordinal)
                               || e.StartsWith("Balance(", StringComparison.Ordinal)
                               || e.StartsWith("Nonce(", StringComparison.Ordinal)
                               || e.StartsWith("Code(", StringComparison.Ordinal))
            .Select(static e => e[(e.IndexOf('(') + 1)..(e.IndexOf('(') + 43)])
            .Distinct()
            .ToArray();

    private static ExecutionCapture Run(
        bool forceEvmPath,
        ulong value,
        RecipientKind recipientKind,
        bool eip7708,
        int dataLength,
        TracerShape tracerShape,
        Mode mode)
    {
        OverridableReleaseSpec spec = new(Prague.Instance) { IsEip7708Enabled = eip7708 };
        TestSpecProvider specProvider = new(spec);
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable worldScope = state.BeginScope(IWorldState.PreGenesis);

        Address recipient = recipientKind switch
        {
            RecipientKind.Existing => TestItem.AddressB,
            RecipientKind.ExistingEmpty => TestItem.AddressB,
            RecipientKind.Nonexistent => TestItem.AddressC,
            RecipientKind.Self => TestItem.AddressA,
            RecipientKind.Delegated => TestItem.AddressB,
            RecipientKind.Precompile => IdentityPrecompile.Address,
            _ => throw new ArgumentOutOfRangeException(nameof(recipientKind))
        };

        state.CreateAccount(TestItem.AddressA, 1.Ether);
        switch (recipientKind)
        {
            case RecipientKind.Existing:
                state.CreateAccount(TestItem.AddressB, 1.Ether);
                break;
            case RecipientKind.ExistingEmpty:
                state.CreateAccount(TestItem.AddressB, UInt256.Zero);
                break;
            case RecipientKind.Delegated:
                state.CreateAccount(TestItem.AddressB, 1.Ether);
                byte[] delegationCode = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressD.Bytes];
                state.InsertCode(TestItem.AddressB, delegationCode, spec);
                break;
        }

        state.Commit(specProvider.GenesisSpec);
        state.CommitTree(0);

        EthereumCodeInfoRepository codeInfoRepository = new(state);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        EthereumEcdsa ecdsa = new(specProvider.ChainId);

        Transaction tx = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(value)
            .WithData(dataLength is 0 ? [] : Enumerable.Repeat((byte)1, dataLength).ToArray())
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithGasLimit(100_000)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithNumber(1)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10_000_000)
            .TestObject;

        bool forceDisabledBefore = SimpleTransferFastPath.ForceDisabled;
        SimpleTransferFastPath.ForceDisabled = forceEvmPath;
        try
        {
            RecordingTracer? recordingTracer = tracerShape is TracerShape.None ? null : new RecordingTracer(tracerShape);
            ITxTracer tracer = recordingTracer is not null ? recordingTracer : NullTxTracer.Instance;
            BlockExecutionContext ctx = new(block.Header, specProvider.GetSpec(block.Header));

            long emptyCallsBefore = Metrics.EmptyCalls;
            TransactionResult result = mode switch
            {
                Mode.Execute => processor.Execute(tx, ctx, tracer),
                Mode.CallAndRestore => processor.CallAndRestore(tx, ctx, tracer),
                Mode.BuildUp => processor.BuildUp(tx, ctx, tracer),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            long fastPathEngaged = Metrics.EmptyCalls - emptyCallsBefore;

            Hash256? stateRoot = null;
            if (mode is Mode.Execute)
            {
                state.CommitTree(1);
                stateRoot = state.StateRoot;
            }

            return new ExecutionCapture(
                Executed: result.TransactionExecuted,
                Status: recordingTracer?.Status ?? 0,
                Error: recordingTracer?.Error,
                GasSpent: recordingTracer?.GasSpent ?? 0,
                EffectiveBlockGas: recordingTracer?.EffectiveBlockGas ?? 0,
                HeaderGasUsed: block.Header.GasUsed,
                StateRoot: stateRoot,
                SenderBalance: state.GetBalance(TestItem.AddressA),
                RecipientBalance: state.GetBalance(recipient),
                FastPathEngaged: fastPathEngaged,
                TracerEvents: recordingTracer?.Events.ToArray() ?? []);
        }
        finally
        {
            SimpleTransferFastPath.ForceDisabled = forceDisabledBefore;
        }
    }

    private sealed class RecordingTracer : TxTracer
    {
        public List<string> Events { get; } = [];
        public byte Status { get; private set; }
        public string? Error { get; private set; }
        public ulong GasSpent { get; private set; }
        public ulong EffectiveBlockGas { get; private set; }

        public RecordingTracer(TracerShape shape)
        {
            IsTracingReceipt = true;
            IsTracingActions = true;
            IsTracingLogs = true;
            IsTracingAccess = true;
            IsTracingFees = true;
            IsTracingRefunds = true;
            IsTracingCode = true;
            if (shape is TracerShape.AllOn)
            {
                IsTracingState = true;
                IsTracingStorage = true;
            }
        }

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) =>
            Events.Add($"Action({gas},{value},{from},{to},{input.Span.ToHexString()},{callType},{isPrecompileCall})");

        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) =>
            Events.Add($"ActionEnd({gas},{output.Span.ToHexString()})");

        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) =>
            Events.Add($"ActionEndDeploy({gas},{deploymentAddress},{deployedCode.Span.ToHexString()})");

        public override void ReportActionError(EvmExceptionType evmExceptionType) =>
            Events.Add($"ActionError({evmExceptionType})");

        public override void ReportByteCode(ReadOnlyMemory<byte> byteCode) =>
            Events.Add($"ByteCode({byteCode.Span.ToHexString()})");

        public override void ReportLog(LogEntry log) =>
            Events.Add($"Log({log.Address},{string.Join('|', log.Topics.Select(static t => t.ToString()))},{log.Data.ToHexString()})");

        public override void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells) =>
            Events.Add($"Access([{string.Join('|', accessedAddresses.Select(static a => a.ToString()).OrderBy(static a => a, StringComparer.Ordinal))}],[{string.Join('|', accessedStorageCells.Select(static c => c.ToString()).OrderBy(static c => c, StringComparer.Ordinal))}])");

        public override void ReportFees(UInt256 fees, UInt256 burntFees) =>
            Events.Add($"Fees({fees},{burntFees})");

        public override void ReportRefund(long refund) =>
            Events.Add($"Refund({refund})");

        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after) =>
            Events.Add($"Balance({address},{before?.ToString() ?? "null"},{after?.ToString() ?? "null"})");

        public override void ReportNonceChange(Address address, UInt256? before, UInt256? after) =>
            Events.Add($"Nonce({address},{before?.ToString() ?? "null"},{after?.ToString() ?? "null"})");

        public override void ReportCodeChange(Address address, byte[]? before, byte[]? after) =>
            Events.Add($"Code({address},{before?.ToHexString() ?? "null"},{after?.ToHexString() ?? "null"})");

        public override void ReportAccountRead(Address address) =>
            Events.Add($"AccountRead({address})");

        public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) =>
            Events.Add($"Storage({storageCell},{before.ToHexString()},{after.ToHexString()})");

        public override void ReportStorageRead(in StorageCell storageCell) =>
            Events.Add($"StorageRead({storageCell})");

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            Status = StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
            EffectiveBlockGas = gasSpent.EffectiveBlockGas;
            Events.Add($"Success({recipient},{gasSpent.SpentGas},{output.ToHexString()},logs:{logs.Length})");
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Status = StatusCode.Failure;
            Error = error;
            GasSpent = gasSpent.SpentGas;
            EffectiveBlockGas = gasSpent.EffectiveBlockGas;
            Events.Add($"Failed({recipient},{gasSpent.SpentGas},{error})");
        }
    }
}
