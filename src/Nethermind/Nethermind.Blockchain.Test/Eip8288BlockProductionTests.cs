// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class Eip8288BlockProductionTests
{
    [Test]
    public async Task Improvement_passes_reuse_verified_proofs_and_changed_dependency_sets_replace_the_cache()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier)
            .AddSingleton(proofs));
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
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        proofs.AddVerified([dependency], [[1]], null);
        Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        Block? first = await chain.BlockProducer.BuildBlock();
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Transactions, Has.Length.EqualTo(1));
        first.Header.RecursiveStark!.StarkProof[0] ^= 0xff;
        Block? second = await chain.BlockProducer.BuildBlock();
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Transactions, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            Assert.That(second.Header.RecursiveStark!.StarkProof,
                Is.EqualTo(Eip8288Dependencies.ComputeBlockDepsHash(second).ToByteArray()));
        }

        Block? empty = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        Assert.That(empty, Is.Not.Null);
        Assert.That(empty!.Transactions, Is.Empty);
        Assert.That(verifier.ProofCalls, Is.EqualTo(2));
        Block? restored = await chain.BlockProducer.BuildBlock();
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Transactions, Has.Length.EqualTo(1));
        Assert.That(verifier.ProofCalls, Is.EqualTo(3));
    }

    private sealed class CountingVerifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof.SequenceEqual(depsHash.Bytes);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            return depsHash.ToByteArray();
        }
    }
}
