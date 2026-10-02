// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

public class ProofWrapperServiceTests
{
    [Test]
    public async Task Oversized_dependency_frames_are_rejected_before_proof_verification([Values] bool inclusionList)
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        byte[] data = Eip8288Dependencies.Serialize(Enumerable.Repeat(dependency, Eip8288Constants.MaxDependenciesPerFrame + 1).ToArray());
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
                (ulong)(Eip8288Constants.MaxDependenciesPerFrame + 1) * Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = inclusionList
            ? await service.AcceptInclusionListAsync(FocilInclusionListDecoder.Instance.Encode(new FocilInclusionList
            {
                Transactions = [transaction], RecursiveStark = recursive
            }).Bytes)
            : await service.AcceptAsync(MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [new WrapperTransaction(transaction)], Deps = [dependency],
                Mode = MempoolWrapper.ModeRecursive, RecursiveStark = recursive
            }).Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.IsSuccess, Is.False);
            Assert.That(accepted.Error, Is.EqualTo(FrameTxValidation.TooManyDependenciesPerFrame));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Shared_dependencies_cannot_create_unbounded_transaction_payloads()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(10, 1024 * 1024);
        byte[] first = service.BuildWrapper().Data!;
        MempoolWrapper decoded = Decode(first);
        byte[] second = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Transactions, Has.Count.EqualTo(7));
            Assert.That(first.Length, Is.LessThan(LeanProofStore.MaxWrapperBytes - Eip8288Constants.MaxProofBytes));
            Assert.That(ValueKeccak.Compute(second), Is.Not.EqualTo(ValueKeccak.Compute(first)));
            Assert.That(verifier.ProofCalls, Is.EqualTo(2));
        }
    }

    [Test]
    public void Rotation_does_not_reprove_an_unchanged_complete_selection()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(3, 64);
        byte[] first = service.BuildWrapper().Data!;
        byte[] second = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueKeccak.Compute(second), Is.EqualTo(ValueKeccak.Compute(first)));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Large_discarded_parent_coverage_is_skipped_before_proving()
    {
        LeanProofStore store = new();
        Transaction[] transactions = new Transaction[2];
        for (int i = 0; i < transactions.Length; i++)
        {
            FrameDependency[] parent = Enumerable.Range(0, 2200).Select(index => new FrameDependency(
                Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"{i}:{index}"), default)).ToArray();
            store.AddVerified(parent, null, [(byte)i]);
            transactions[i] = new Transaction
            {
                Type = TxType.FrameTx, ChainId = 1, SenderAddress = Address.Zero,
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([parent[0]]))]
            };
            transactions[i].Hash = transactions[i].CalculateHash();
        }
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService(transactions, store, verifier);
        byte[] first = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Decode(first).Transactions, Has.Count.EqualTo(1));
            Assert.That(verifier.LargestRecursiveInput, Is.EqualTo(1));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        }
    }

    private static (ProofWrapperService, FakeLeanProofVerifier) Create(int count, int payloadBytes)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        byte[] payload = new byte[payloadBytes];
        Transaction[] transactions = Enumerable.Range(0, count).Select(index => new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Nonce = (ulong)index,
            Frames =
            [
                new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency])),
                new(FrameMode.Sender, FrameFlags.None, Address.Zero, 100_000, UInt256.Zero, payload)
            ]
        }).ToArray();
        foreach (Transaction transaction in transactions) transaction.Hash = transaction.CalculateHash();
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier verifier = new(true);
        return (CreateService(transactions, store, verifier), verifier);
    }

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, FakeLeanProofVerifier verifier)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
    }

    private static MempoolWrapper Decode(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }
}
