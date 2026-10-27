// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public partial class EthRpcModuleTests
{
    [Test]
    public async Task Proof_wrapper_methods_are_registered_and_fork_gated([Values("eth_sendProofWrapper", "eth_getProofWrapper")] string method)
    {
        using Context context = await Context.Create();
        string response = method == "eth_getProofWrapper"
            ? await context.Test.TestEthRpc(method)
            : await context.Test.TestEthRpc(method, "0x00");
        Assert.That(response, Does.Contain("unavailable"));
        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.MethodNotFound}"));
    }

    [Test]
    public async Task Proof_wrapper_ingress_rejects_malformed_encoding()
    {
        using Context context = await ProofContext(new RecordingProofVerifier());
        string response = await context.Test.TestEthRpc("eth_sendProofWrapper", "0x00");
        Assert.That(response, Does.Contain("Invalid proof"));
        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.TransactionRejected}"));
    }

    [Test]
    public async Task Proof_wrapper_ingress_resolves_pending_hashes_and_egress_proves_witnesses([Values] bool zeroDataHash)
    {
        RecordingProofVerifier verifier = new();
        using Context context = await ProofContext(verifier);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, zeroDataHash ? default : TestItem.KeccakA.ValueHash256, default);
        Transaction transaction = ProofTransaction(context, dependency);
        byte[] frameData = transaction.Frames![1].Data.ToArray();
        MempoolWrapper direct = new()
        {
            Transactions = [new WrapperTransaction(transaction)],
            Mode = MempoolWrapper.ModeDirect,
            Deps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue),
            Proofs = [[7]]
        };
        ResultWrapper<Hash256[]> received = await context.Test.EthRpcModule.eth_sendProofWrapper(MempoolWrapperDecoder.Instance.Encode(direct).Bytes,
            [Rlp.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes]);
        Assert.That(received.Result, Is.EqualTo(Result.Success), received.Result.Error);
        Assert.That(context.Test.TxPool.TryGetPendingTransaction(received.Data[0].ValueHash256, out _), Is.True);
        MempoolWrapper hashOnly = new()
        {
            Transactions = [new WrapperTransaction(received.Data[0])],
            Mode = MempoolWrapper.ModeDirect,
            Deps = direct.Deps,
            Proofs = direct.Proofs
        };
        ResultWrapper<Hash256[]> resolved = await context.Test.EthRpcModule.eth_sendProofWrapper(MempoolWrapperDecoder.Instance.Encode(hashOnly).Bytes);
        Assert.That(resolved.Result, Is.EqualTo(Result.Success), resolved.Result.Error);
        int proofCalls = verifier.ProofCalls;
        Assert.That(context.Test.EthRpcModule.eth_getProofWrapper().Result.Error, Does.Contain("not ready"));
        Assert.That(verifier.ProofCalls, Is.EqualTo(proofCalls));
        Assert.That(context.Test.Container.Resolve<ProofWrapperService>().BuildWrapper().IsSuccess, Is.True);
        ResultWrapper<byte[]> aggregated = context.Test.EthRpcModule.eth_getProofWrapper();
        Assert.That(aggregated.Result, Is.EqualTo(Result.Success), aggregated.Result.Error);
        Assert.That(verifier.LastInput!.Witnesses[0].ToArray(), Is.EqualTo(new byte[] { 7 }));
        RlpReader reader = new(aggregated.Data);
        MempoolWrapper output = MempoolWrapperDecoder.Instance.Decode(ref reader)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
            Assert.That(output.Transactions, Has.Count.EqualTo(1));
            Assert.That(output.Deps, Is.EqualTo(direct.Deps));
            Assert.That(output.Deps[0].DataHash, Is.EqualTo(zeroDataHash ? FrameTxSigHash.ComputeValue(transaction) : dependency.DataHash));
            Assert.That(output.RecursiveStark!.BlockDepsHash.ValueHash256, Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(direct.Deps)));
            Assert.That(transaction.Frames[1].Data.ToArray(), Is.EqualTo(frameData));
        }
    }

    [Test]
    public async Task Proof_wrapper_ingress_rejects_unknown_transaction_hash()
    {
        using Context context = await ProofContext(new RecordingProofVerifier());
        MempoolWrapper wrapper = new() { Transactions = [new WrapperTransaction(Keccak.Compute("unknown"))], Mode = MempoolWrapper.ModeDirect, Deps = [new(Eip8288Constants.LeanSphincsScheme, default, default)], Proofs = [[1]] };
        ResultWrapper<Hash256[]> response = await context.Test.EthRpcModule.eth_sendProofWrapper(MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes);
        Assert.That(response.Result.Error, Is.EqualTo(MempoolWrapperValidator.UnknownTransaction));
    }

    [TestCase("duplicate")]
    [TestCase("empty")]
    [TestCase("too-many")]
    [TestCase("unclaimed")]
    public async Task Wrapper_recovery_rejects_invalid_envelopes_before_proof_verification(string scenario)
    {
        RecordingProofVerifier verifier = new();
        using Context context = await ProofContext(verifier);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = ProofTransaction(context, dependency);
        byte[] envelope = Rlp.Encode(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes;
        byte[][] envelopes = scenario switch
        {
            "duplicate" => [envelope, envelope],
            "empty" => [[]],
            "too-many" => new byte[Eip8288Constants.MaxTxsPerWrapper + 1][],
            _ => [envelope]
        };
        Hash256 hash = scenario == "unclaimed" ? Keccak.Compute("another-transaction") : transaction.Hash!;
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(hash)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ResultWrapper<Hash256[]> result = await context.Test.EthRpcModule.eth_sendProofWrapper(wrapper, envelopes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result, Is.Not.EqualTo(Result.Success));
            Assert.That(verifier.VerificationCalls, Is.Zero);
            Assert.That(context.Test.TxPool.GetPendingTransactions(), Is.Empty);
        }
    }

    private static Transaction ProofTransaction(Context context, FrameDependency dependency)
    {
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = context.Test.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    private static Task<Context> ProofContext(ILeanProofVerifier verifier) => Context.Create(
        new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), configurer: builder => builder.AddSingleton(verifier));

    private sealed class RecordingProofVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public int ProofCalls { get; private set; }
        public int VerificationCalls { get; private set; }

        public AggregationInput? LastInput { get; private set; }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) { VerificationCalls++; return !witness.IsEmpty; }
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) { VerificationCalls++; return !proof.IsEmpty; }
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            LastInput = input;
            return [1];
        }
    }
}
