// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// A KZG setup fault is a node fault, so it must escape the precompile dispatch that turns every other
/// precompile exception into a failed call. Both dispatch paths are covered: CALL runs a precompile in a call
/// frame, and an untraced STATICCALL runs it inline.
/// </summary>
public class KzgSetupFaultTests : VirtualMachineTestsBase
{
    private const int CallGas = 100_000;
    private const int PointEvaluationInputLength = 192;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

    [OneTimeSetUp]
    public Task LoadKzgSetup() => KzgPolynomialCommitments.InitializeAsync();

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.STATICCALL)]
    public void Setup_fault_escapes_the_point_evaluation_call(Instruction call)
    {
        UsePointEvaluationPrecompile(new ThrowingPrecompile(new KzgSetupUnavailableException("KZG trusted setup failed to load")));

        Assert.That(() => ExecuteCall(call, new byte[PointEvaluationInputLength]), Throws.TypeOf<KzgSetupUnavailableException>());
    }

    [TestCase(Instruction.CALL)]
    [TestCase(Instruction.STATICCALL)]
    public void Other_precompile_exception_still_fails_the_call(Instruction call)
    {
        UsePointEvaluationPrecompile(new ThrowingPrecompile(new InvalidOperationException("malformed input")));

        Assert.That(ExecuteCall(call, new byte[PointEvaluationInputLength]), Is.False);
    }

    [TestCaseSource(nameof(MalformedPointEvaluationCalls))]
    public void Malformed_point_evaluation_input_fails_the_call(Instruction call, byte[] input) =>
        Assert.That(ExecuteCall(call, input), Is.False);

    private static IEnumerable<TestCaseData> MalformedPointEvaluationCalls()
    {
        // The commitment is not a curve point, so the proof check itself throws and reports it as invalid.
        byte[] offCurve = new byte[PointEvaluationInputLength];
        offCurve.AsSpan(96, 48).Fill(0xff);
        KzgPolynomialCommitments.TryComputeCommitmentHashV1(offCurve.AsSpan(96, 48), offCurve.AsSpan(0, 32));

        foreach (Instruction call in (Instruction[])[Instruction.CALL, Instruction.STATICCALL])
        {
            yield return new TestCaseData(call, new byte[PointEvaluationInputLength - 1]).SetArgDisplayNames(call.ToString(), "short input");
            yield return new TestCaseData(call, new byte[PointEvaluationInputLength]).SetArgDisplayNames(call.ToString(), "hash mismatch");
            yield return new TestCaseData(call, offCurve).SetArgDisplayNames(call.ToString(), "off-curve commitment");
        }
    }

    private void UsePointEvaluationPrecompile(IPrecompile precompile)
    {
        Dictionary<AddressAsKey, CodeInfo> precompiles = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [KzgPointEvaluationPrecompile.Address] = new(precompile)
        };
        IPrecompileProvider provider = new FixedPrecompileProvider(precompiles.ToFrozenDictionary());
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine,
            new CacheCodeInfoRepository(TestState, provider, new StaticCodeCache(16)), LimboLogs.Instance);
    }

    /// <returns>Whether the call to the point evaluation precompile succeeded.</returns>
    private bool ExecuteCall(Instruction call, byte[] input)
    {
        Prepare code = Prepare.EvmCode
            .StoreDataInMemory(0, input)
            .PushData(0)
            .PushData(0)
            .PushData(input.Length)
            .PushData(0);
        if (call == Instruction.CALL) code.PushData(0);
        byte[] bytecode = code
            .PushData(KzgPointEvaluationPrecompile.Address)
            .PushData(CallGas)
            .Op(call)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        // Untraced, so an eligible STATICCALL takes the inline precompile path.
        (Block block, Transaction transaction) = PrepareTx(Activation, 2 * CallGas, bytecode);
        TransactionResult result = _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), NullTxTracer.Instance);
        Assert.That(result.TransactionExecuted, Is.True, result.ToString());

        TestState.Get(new StorageCell(Recipient, UInt256.Zero), out UInt256 callSucceeded);
        return !callSucceeded.IsZero;
    }

    private sealed class ThrowingPrecompile(Exception exception) : IPrecompile
    {
        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 50_000;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => throw exception;
    }

    private sealed class FixedPrecompileProvider(FrozenDictionary<AddressAsKey, CodeInfo> precompiles) : IPrecompileProvider
    {
        public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => precompiles;
    }
}
