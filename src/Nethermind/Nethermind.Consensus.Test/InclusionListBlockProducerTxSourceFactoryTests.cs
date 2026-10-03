// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Test.Eip8288;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class InclusionListBlockProducerTxSourceFactoryTests
{
    [Test]
    public void Proven_frames_and_legacy_inclusion_entries_share_the_existing_pipeline()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance)
        {
            IsEip8141Enabled = true,
            IsEip8288Enabled = true,
            IsEip8250Enabled = true,
            IsEip8272Enabled = true,
            IsEip7906Enabled = true
        };
        Transaction legacy = Build.A.Transaction.WithNonce(3).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction mempool = Build.A.Transaction.WithNonce(7).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
        dependency[31] = Eip8288Constants.LeanSphincsScheme;
        TxFrame[] frames = [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, dependency),
            new(FrameMode.PostTx, FrameFlags.None, TestItem.AddressD, 10_000, UInt256.Zero, default)];
        Transaction frame = new()
        {
            Type = TxType.FrameTx,
            ChainId = MainnetSpecProvider.Instance.ChainId,
            SenderAddress = TestItem.PrivateKeyA.Address,
            NonceKeys = [0],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1,
            DecodedMaxFeePerGas = 1
        };
        FrameTxTestFrames.SignSecp256k1(frame, TestItem.PrivateKeyA, frame.SenderAddress);
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(frame);
        byte[] proven = Eip8288Dependencies.Serialize(dependencies);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)));
        LeanProofStore store = new();
        FakeLeanProofVerifier verifier = new(true);
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance, verifier, store);
        byte[][] list = [TxDecoder.Instance.Encode(legacy, RlpBehaviors.SkipTypedWrapping).Bytes,
            TxDecoder.Instance.Encode(frame, RlpBehaviors.SkipTypedWrapping).Bytes];
        source.Set(list, spec, proof, proven);
        PayloadAttributes attributes = new() { InclusionListTransactions = list, InclusionListRecursiveStark = proof, InclusionListProvenDependencies = proven };
        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempool]);
        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);
        ITxSource combined = new InclusionListBlockProducerTxSourceFactory(baseFactory, source).Create();
        Transaction[] selected = [.. combined.GetTransactions(Build.A.BlockHeader.WithNumber(0).TestObject,
            Build.A.BlockHeader.WithNumber(1).TestObject, 30_000_000, attributes)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selected.Select(tx => tx.SenderAddress), Is.EqualTo([
                TestItem.PrivateKeyB.Address, TestItem.PrivateKeyA.Address, TestItem.PrivateKeyC.Address]));
            Assert.That(selected[1].NonceKeys, Is.EqualTo((UInt256[])[0]));
            Assert.That(selected[1].Frames![^1].Mode, Is.EqualTo(FrameMode.PostTx));
            Assert.That(store.Covers(selected[1]), Is.True);
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void IL_transactions_precede_mempool_transactions_in_pipeline()
    {
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        Transaction ilTx = Build.A.Transaction
            .WithNonce(3)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance,
            Substitute.For<Nethermind.Core.Crypto.ILeanProofVerifier>());
        byte[][] ilBytes = [TxDecoder.Instance.Encode(ilTx, RlpBehaviors.SkipTypedWrapping).Bytes];
        il.Set(ilBytes, Bogota.Instance);
        // IL is keyed by the build's PayloadAttributes array.
        PayloadAttributes payloadAttributes = new() { InclusionListTransactions = ilBytes };

        ITxSource txSource = new InclusionListBlockProducerTxSourceFactory(baseFactory, il).Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(parent.Number + 1).TestObject;
        List<Transaction> selectedTxs = [.. txSource.GetTransactions(parent, targetBlock, 30_000_000UL, payloadAttributes)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectedTxs, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(selectedTxs[0].Nonce, Is.EqualTo(3UL), "the IL transaction must be selected before the mempool source");
            Assert.That(selectedTxs.Any(t => t.Nonce == 7UL), Is.True, "the mempool transaction must still appear after the IL");
        }
    }

    [Test]
    public void Empty_IL_still_drains_mempool()
    {
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(7)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        ITxSource mempoolSource = Substitute.For<ITxSource>();
        mempoolSource.GetTransactions(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<PayloadAttributes>(), Arg.Any<bool>())
            .Returns([mempoolTx]);

        IBlockProducerTxSourceFactory baseFactory = Substitute.For<IBlockProducerTxSourceFactory>();
        baseFactory.Create().Returns(mempoolSource);

        InclusionListTxSource il = new(
            new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
            LimboLogs.Instance,
            Substitute.For<Nethermind.Core.Crypto.ILeanProofVerifier>());
        // IL is empty

        ITxSource txSource = new InclusionListBlockProducerTxSourceFactory(baseFactory, il).Create();

        BlockHeader parent = Build.A.BlockHeader.TestObject;
        BlockHeader targetBlock = Build.A.BlockHeader.WithNumber(parent.Number + 1).TestObject;
        List<Transaction> selectedTxs = [.. txSource.GetTransactions(parent, targetBlock, 30_000_000UL)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selectedTxs, Has.Count.EqualTo(1));
            Assert.That(selectedTxs[0].Nonce, Is.EqualTo(7UL));
        }
    }
}
