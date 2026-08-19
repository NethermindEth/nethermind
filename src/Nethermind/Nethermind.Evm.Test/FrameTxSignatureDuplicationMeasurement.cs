// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Sizes the signature verification <see cref="ExecutionOptions.FrameSignaturesPreValidated"/> removes from a
/// validation-prefix simulation, against the EVM run that follows it.
/// </summary>
/// <remarks>
/// The pool serializes prefix simulations behind one lock, so the interesting quantity is not the ratio but the
/// microseconds the duplicate pass adds to the hold. Emits one CSV row per shape to the path in
/// <c>FRAME_SIGDUP_OUT</c> (default <c>frame-sig-duplication.csv</c> under the temp directory), because the test
/// runner swallows console writers. Timings are the best of repeated batches; the simulator's own read-only scope
/// build is outside what is timed here, so the real hold is longer than the EVM column.
/// </remarks>
[Explicit("measurement harness")]
public class FrameTxSignatureDuplicationMeasurement
{
    private const int WarmupIterations = 100;
    private const int Batches = 15;
    private const int BatchIterations = 100;

    /// <summary>Gas burnt by one PUSH/PUSH/ADD/POP round of the prefix contract's filler loop.</summary>
    private const int GasPerLoop = 11;

    private static readonly Address Sender = TestItem.AddressA;

    private ISpecProvider _specProvider = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _worldStateCloser = null!;
    private readonly IEthereumEcdsa _ethereumEcdsa = new EthereumEcdsa(TestBlockchainIds.ChainId);
    private readonly Ecdsa _ecdsa = new();
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    [Test]
    public void MeasureTheDuplicateSignaturePass()
    {
        DeployPrefixContract(burnLoops: 0);
        Emit("shape,signatures,duplicate_pass_us,evm_prefix_us,combined_us");

        // 107 SECP256K1 / 44 P256 entries are what MAX_VERIFY_GAS admits when the frames themselves are free.
        foreach (int count in new[] { 0, 1, 2, 8, 107 }) Measure($"secp256k1", count, SecpSignedTx(count));
        foreach (int count in new[] { 1, 44 }) Measure("p256", count, P256SignedTx(count));

        foreach (int loops in new[] { 0, 90, 455, 1_820, 4_500, 9_000 })
        {
            DeployPrefixContract(loops);
            Measure($"prefix-{loops * GasPerLoop}gas", 1, SecpSignedTx(1));
        }
    }

    private void Measure(string shape, int signatures, Transaction tx)
    {
        double duplicate = BestUs(() =>
        {
            ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
            if (!FrameTxSignatureValidator.Validate(tx, in sigHash, _ethereumEcdsa, SecP256r1Precompile.Instance, Spec, out string? error))
                throw new InvalidOperationException(error);
        });

        Block block = Build.A.Block.WithNumber(1).WithBaseFeePerGas(0).WithTransactions(tx).WithGasLimit(30_000_000).TestObject;
        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, Spec));

        // Alternate the order so tiered compilation cannot bias one column against the other.
        double combined = BestUs(() => Simulate(tx, ExecutionOptions.None));
        double evm = BestUs(() => Simulate(tx, ExecutionOptions.FrameSignaturesPreValidated));
        combined = Math.Min(combined, BestUs(() => Simulate(tx, ExecutionOptions.None)));
        evm = Math.Min(evm, BestUs(() => Simulate(tx, ExecutionOptions.FrameSignaturesPreValidated)));

        Emit($"{shape},{signatures},{duplicate:F1},{evm:F1},{combined:F1}");
    }

    private void Simulate(Transaction tx, ExecutionOptions extraOptions)
    {
        FrameTxValidationTracer tracer = new(tx.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress, _stateProvider, Spec);
        TransactionResult result = _transactionProcessor.Process(tx, tracer, ExecutionOptions.FrameValidationPrefixOnly | extraOptions);
        if (!result || tracer.Payer is null) throw new InvalidOperationException($"prefix did not resolve a payer: {result.ErrorDescription} {tracer.ViolationReason}");
    }

    private static double BestUs(Action action)
    {
        for (int i = 0; i < WarmupIterations; i++) action();

        double best = double.MaxValue;
        for (int batch = 0; batch < Batches; batch++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < BatchIterations; i++) action();
            stopwatch.Stop();
            best = Math.Min(best, stopwatch.Elapsed.TotalMicroseconds / BatchIterations);
        }

        return best;
    }

    private Transaction SecpSignedTx(int count)
    {
        Transaction tx = FrameTx();
        if (count == 0) return tx;

        // compute_sig_hash covers scheme/signer/msg and elides the raw bytes of canonical-hash entries,
        // so every entry is signed against the hash taken over the placeholders.
        tx.FrameSignatures = Repeat(count, new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, default));
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);
        Signature signature = _ecdsa.Sign(TestItem.PrivateKeyB, in sigHash);

        byte[] vrs = new byte[TxFrameSignature.Secp256k1SignatureLength];
        vrs[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(vrs.AsSpan(1));
        tx.FrameSignatures = Repeat(count, new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyB.Address, default, vrs));
        return tx;
    }

    private static Transaction P256SignedTx(int count)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        byte[] qx = Pad32(parameters.Q.X!);
        byte[] qy = Pad32(parameters.Q.Y!);
        Address signer = new(Keccak.Compute([.. qx, .. qy]).Bytes[12..]);

        Transaction tx = FrameTx();
        tx.FrameSignatures = Repeat(count, new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, default));
        ValueHash256 sigHash = FrameTxSigHash.ComputeValue(tx);

        byte[] raw = new byte[TxFrameSignature.P256SignatureLength];
        key.SignHash(sigHash.Bytes).CopyTo(raw.AsSpan(0)); // IEEE P1363: r || s
        qx.CopyTo(raw.AsSpan(64));
        qy.CopyTo(raw.AsSpan(96));
        UInt256 s = new(raw.AsSpan(32, 32), isBigEndian: true);
        if (s > SecP256r1Curve.HalfN) (SecP256r1Curve.N - s).ToBigEndian(raw.AsSpan(32, 32));

        tx.FrameSignatures = Repeat(count, new TxFrameSignature(TxFrameSignature.SchemeP256, signer, default, raw));
        return tx;
    }

    private static TxFrameSignature[] Repeat(int count, TxFrameSignature signature)
    {
        TxFrameSignature[] signatures = new TxFrameSignature[count];
        Array.Fill(signatures, signature);
        return signatures;
    }

    private static byte[] Pad32(byte[] value)
    {
        if (value.Length == 32) return value;
        byte[] padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }

    private void DeployPrefixContract(int burnLoops)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < burnLoops; i++) code = code.PushData(1).PushData(2).Op(Instruction.ADD).Op(Instruction.POP);
        byte[] approve = code.PushData(TxFrame.ApproveExecutionAndPayment).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

        if (!_stateProvider.AccountExists(Sender)) _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.InsertCode(Sender, approve, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);
    }

    private static Transaction FrameTx() =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 250_000, UInt256.Zero, default)],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_SIGDUP_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-sig-duplication.csv");
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
