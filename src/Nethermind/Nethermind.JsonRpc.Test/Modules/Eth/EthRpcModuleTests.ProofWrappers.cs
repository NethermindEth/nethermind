// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
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
    public async Task Proof_wrapper_methods_are_registered_and_fork_gated([Values("eth_sendProofWrapper", "eth_sendProofInclusionList", "eth_getProofWrapper")] string method)
    {
        using Context context = await Context.Create();
        string response = method == "eth_getProofWrapper"
            ? await context.Test.TestEthRpc(method)
            : await context.Test.TestEthRpc(method, "0x00");
        Assert.That(response, Does.Contain("unavailable"));
        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.MethodNotFound}"));
    }

    [Test]
    public async Task Proof_wrapper_methods_reject_malformed_encoding([Values("eth_sendProofWrapper", "eth_sendProofInclusionList")] string method)
    {
        using Context context = await ProofContext(new RecordingProofVerifier());
        string response = await context.Test.TestEthRpc(method, "0x00");
        Assert.That(response, Does.Contain("Invalid proof"));
        Assert.That(response, Does.Contain($"\"code\":{ErrorCodes.TransactionRejected}"));
    }

    [Test]
    public async Task Proof_wrapper_ingress_resolves_pending_hashes_and_egress_proves_witnesses()
    {
        RecordingProofVerifier verifier = new();
        using Context context = await ProofContext(verifier);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = context.Test.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        MempoolWrapper direct = new()
        {
            Transactions = [new WrapperTransaction(transaction)],
            Mode = MempoolWrapper.ModeDirect,
            Deps = [dependency],
            Proofs = [[7]]
        };
        ResultWrapper<Hash256[]> received = await context.Test.EthRpcModule.eth_sendProofWrapper(MempoolWrapperDecoder.Instance.Encode(direct).Bytes);
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
        ResultWrapper<byte[]> aggregated = context.Test.EthRpcModule.eth_getProofWrapper();
        Assert.That(aggregated.Result, Is.EqualTo(Result.Success), aggregated.Result.Error);
        Assert.That(verifier.LastInput!.Witnesses[0], Is.EqualTo(new byte[] { 7 }));
        RlpReader reader = new(aggregated.Data);
        MempoolWrapper output = MempoolWrapperDecoder.Instance.Decode(ref reader)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(output.Mode, Is.EqualTo(MempoolWrapper.ModeRecursive));
            Assert.That(output.Transactions, Has.Count.EqualTo(1));
            Assert.That(output.Deps, Is.EqualTo(direct.Deps));
            Assert.That(output.RecursiveStark!.BlockDepsHash.ValueHash256, Is.EqualTo(Eip8288Dependencies.ComputeDepsHash(direct.Deps)));
        }
    }

    [Test]
    public async Task Proof_wrapper_ingress_rejects_unknown_transaction_hash()
    {
        using Context context = await ProofContext(new RecordingProofVerifier());
        MempoolWrapper wrapper = new() { Transactions = [new WrapperTransaction(Keccak.Compute("unknown"))], Mode = MempoolWrapper.ModeDirect, Deps = [], Proofs = [] };
        ResultWrapper<Hash256[]> response = await context.Test.EthRpcModule.eth_sendProofWrapper(MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes);
        Assert.That(response.Result.Error, Is.EqualTo(MempoolWrapperValidator.UnknownTransaction));
    }

    private static Task<Context> ProofContext(ILeanProofVerifier verifier) => Context.Create(
        new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), configurer: builder => builder.AddSingleton(verifier));

    private sealed class RecordingProofVerifier : ILeanProofVerifier
    {
        public AggregationInput? LastInput { get; private set; }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => !witness.IsEmpty;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => !proof.IsEmpty;
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            LastInput = input;
            return [1];
        }
    }
}
