// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeBlockProductionTests
{
    [Test]
    public async Task Zero_dependency_hash_proves_the_final_signature_hash_through_wrapper_and_block([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        LeanProofStore proofs = new(FrameTxSigHash.ComputeValue);
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(NativeLeanProofVerifier.Instance)
            .AddSingleton(proofs));
        FrameDependency raw = new(Eip8288Constants.LeanSphincsScheme, default, NativeLeanProofVerifierTests.Dependency("sphincs").VerificationKey);
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([raw]))];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        byte[] encodedTransaction = TxDecoder.Instance.Encode(transaction).Bytes;
        FrameDependency resolved = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue).Single();
        byte[] witness = await SignMessage(resolved.DataHash);
        ILeanProofVerifier verifier = NativeLeanProofVerifier.Instance;
        Assert.That(verifier.VerifyLeanSphincs(resolved.DataHash, resolved.VerificationKey, witness), Is.True);
        Assert.That(verifier.VerifyLeanSphincs(default, resolved.VerificationKey, witness), Is.False);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash([resolved]);
        MempoolWrapper wrapper = new()
        {
            Transactions = [transaction],
            Deps = [resolved],
            Mode = mode,
            Proofs = mode == MempoolWrapper.ModeDirect ? [witness] : null,
            RecursiveStark = mode == MempoolWrapper.ModeRecursive
                ? new(RecursiveStarkAggregator.Prove(new AggregationInput { Deps = [resolved], Witnesses = [witness] }, verifier, Eip8288Verifier.Pinned, in depsHash), new Hash256(depsHash))
                : null
        };
        ProofWrapperAcceptance acceptance = await chain.Container.Resolve<ProofWrapperService>().AcceptDetailedAsync(
            MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes);
        Assert.That(acceptance.Result.IsSuccess, Is.True, acceptance.Result.Error);
        using CancellationTokenSource provingBudget = new(TimeSpan.FromMinutes(3));
        if (mode == MempoolWrapper.ModeDirect)
        {
            // A pass with a deadline never waits for native proving; it schedules the statement off the production path.
            using CancellationTokenSource deadline = new();
            Block first = (await chain.BlockProducer.BuildBlock(cancellationToken: deadline.Token))!;
            Assert.That(first.Transactions, Is.Empty);
            Assert.That(() => proofs.TryGetRecursiveProof([resolved], out _), Is.True.After(180_000, 100));
        }

        Block block = await chain.Container.Resolve<TestBlockchainUtil>().AddBlock(
            TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx, provingBudget.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(1));
            Assert.That(block.Header.RecursiveStark!.BlockDepsHash, Is.EqualTo(new Hash256(depsHash)));
            Assert.That(verifier.VerifyRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, block.StarkProof!), Is.True);
            Assert.That(verifier.VerifyRecursiveStark(Eip8288Dependencies.ComputeDepsHash([raw]), Eip8288Constants.AggregatedVk, block.StarkProof!), Is.False);
            Assert.That(Eip8288Dependencies.ParseFrame(transaction.Frames![1]).Single(), Is.EqualTo(raw));
            Assert.That(TxDecoder.Instance.Encode(transaction).Bytes, Is.EqualTo(encodedTransaction));
        }
        Transaction changed = new();
        transaction.CopyTo(changed);
        changed.Nonce++;
        ValueHash256 changedHash = FrameTxSigHash.ComputeValue(changed);
        Assert.That(changedHash, Is.Not.EqualTo(resolved.DataHash));
        Assert.That(verifier.VerifyLeanSphincs(changedHash, resolved.VerificationKey, witness), Is.False);
        changed.Hash = changed.CalculateHash();
        MempoolWrapper changedWrapper = new()
        {
            Transactions = [changed],
            Deps = wrapper.Deps,
            Mode = wrapper.Mode,
            Proofs = wrapper.Proofs,
            RecursiveStark = wrapper.RecursiveStark
        };
        Assert.That(MempoolWrapperValidator.Validate(changedWrapper, verifier, Eip8288Verifier.Pinned, out string error), Is.False);
        Assert.That(error, Is.EqualTo(MempoolWrapperValidator.DepsMismatch));
    }

    private static async Task<byte[]> SignMessage(ValueHash256 message)
    {
        string executable = Path.Combine(TestContext.CurrentContext.TestDirectory, "lean-vectors", OperatingSystem.IsWindows() ? "fixtures.exe" : "fixtures");
        string output = Path.GetTempFileName();
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--sign-message", Convert.ToHexString(message.Bytes), output }
            }
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        bool started = false;
        try
        {
            started = process.Start();
            Assert.That(started, Is.True);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.That(process.ExitCode, Is.Zero, await stderr + await stdout);
            return await File.ReadAllBytesAsync(output, timeout.Token);
        }
        finally
        {
            if (started && !process.HasExited) process.Kill(entireProcessTree: true);
            File.Delete(output);
        }
    }

    [Test]
    public async Task Produces_and_validates_real_dependency_block([Values] bool preAggregate)
    {
        FrameDependency signature = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency second = NativeLeanProofVerifierTests.Dependency("second");
        LeanProofStore proofs = new(FrameTxSigHash.ComputeValue);
        CountingNativeVerifier verifier = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier)
            .AddSingleton(proofs));
        Transaction transaction = CreateTransaction(chain);
        AcceptTxResult missing = chain.TxPool.SubmitTx(transaction, TxHandlingOptions.None);
        Assert.That(missing.ToString(), Does.Contain("MissingDependencyProof"));

        byte[][] witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("second")];
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanSphincs(signature.DataHash, signature.VerificationKey, witnesses[0]), Is.True);
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanSphincs(second.DataHash, second.VerificationKey, witnesses[1]), Is.True);
        proofs.AddVerified([signature, second], witnesses, null);
        transaction.Hash = transaction.CalculateHash();
        ValidationResult wellFormed = new TxValidator(chain.SpecProvider.ChainId).IsWellFormed(transaction, Eip8288Prototype.Instance);
        Assert.That((bool)wellFormed, Is.True, wellFormed.ToString());
        AcceptTxResult accepted = chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        Assert.That(accepted, Is.EqualTo(AcceptTxResult.Accepted), accepted.ToString());
        // Fresh native proving can exceed the default test-chain deadline.
        using CancellationTokenSource provingBudget = new(TimeSpan.FromMinutes(3));
        if (preAggregate)
        {
            Result<byte[]> wrapper = chain.Container.Resolve<ProofWrapperService>().BuildWrapper(cancellationToken: provingBudget.Token);
            Assert.That(wrapper.IsSuccess, Is.True, wrapper.Error);
        }
        int callsBeforeProduction = verifier.ProofCalls;
        if (!preAggregate)
        {
            // A pass with a deadline never waits for native proving; it schedules the statement off the production path.
            using CancellationTokenSource deadline = new();
            Block first = (await chain.BlockProducer.BuildBlock(cancellationToken: deadline.Token))!;
            Assert.That(first.Transactions, Is.Empty);
            List<FrameDependency> deps = Eip8288Dependencies.Canonicalize([signature, second]);
            Assert.That(() => proofs.TryGetRecursiveProof(deps, out _), Is.True.After(180_000, 100));
        }
        Block block = await chain.Container.Resolve<TestBlockchainUtil>()
            .AddBlock(TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx, provingBudget.Token);
        ValueHash256 commitment = Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        Assert.That(block.Header.RecursiveStark, Is.Not.Null);
        Assert.That(block.Header.GasUsedPerDimension, Is.Not.Null);
        NativeLeanProofVerifierTests.AssertBareStarkProof(block.StarkProof!, Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue));
        (ulong Execution, ulong State) dimensions = block.Header.GasUsedPerDimension!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.ForBlock(block, FrameTxSigHash.ComputeValue), Has.Count.EqualTo(2));
            Assert.That(block.Header.RecursiveStark!.StarkProofHash, Is.EqualTo(Keccak.Compute(block.StarkProof!)));
            Assert.That(block.GasUsed, Is.LessThanOrEqualTo(block.GasLimit));
            Assert.That(Math.Max(dimensions.Execution, dimensions.State), Is.EqualTo(block.GasUsed));
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(commitment, Eip8288Constants.AggregatedVk,
                block.StarkProof!), Is.True);
            Assert.That(chain.ReceiptStorage.Get(block)[0].GasUsed, Is.EqualTo(block.GasUsed));
            Assert.That(verifier.ProofCalls - callsBeforeProduction, Is.EqualTo(preAggregate ? 0 : 1));
        }
    }

    private sealed class CountingNativeVerifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanSphincs(in dataHash, in verificationKey, witness);
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
            => NativeLeanProofVerifier.Instance.VerifyRecursiveStark(in depsHash, aggregatedVk, proof);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            return NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, aggregatedVk, input);
        }
    }
    internal static Transaction CreateTransaction(BasicTestBlockchain chain)
    {
        FrameDependency signature = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency second = NativeLeanProofVerifierTests.Dependency("second");
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null,
                3 * Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([signature, second, signature]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei,
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

}
