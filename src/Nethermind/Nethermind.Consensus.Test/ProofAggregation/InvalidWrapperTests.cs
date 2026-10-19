// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.ProofAggregation;
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

namespace Nethermind.Consensus.Test.ProofAggregation;

/// <summary>
/// The invalid wrapper shapes the devnet spammer sends, checked against EIP-8437's classification: malformed encodings and
/// invalid proofs are invalid, while unresolved envelopes leave a wrapper incomplete rather than invalid.
/// </summary>
public class InvalidWrapperTests
{
    private static readonly FrameDependency First = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
    private static readonly FrameDependency Second = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakC.ValueHash256, TestItem.KeccakD.ValueHash256);

    private static Transaction Tx(ulong nonce, params FrameDependency[] deps)
    {
        List<TxFrame> frames = [];
        if (deps.Length > 0)
            frames.Add(new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas * (ulong)deps.Length,
                UInt256.Zero, Eip8288Dependencies.Serialize(deps)));
        frames.Add(new(FrameMode.Sender, FrameFlags.None, Address.Zero, 50_000, UInt256.Zero, default));
        Transaction tx = new() { Type = TxType.FrameTx, NonceKeys = [UInt256.Zero], ChainId = 1, SenderAddress = Address.Zero, Nonce = nonce, Frames = [.. frames] };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static ProofWrapperService Service(bool proofsValid)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([]);
        pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(false);
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, new LeanProofStore(FrameTxSigHash.ComputeValue),
            new FakeLeanProofVerifier(proofsValid));
    }

    /// <summary>Encodes without the encoder's ordering checks, as a misbehaving peer or client could.</summary>
    private static byte[] Encode(IEnumerable<Hash256> hashes, IReadOnlyList<FrameDependency> deps)
    {
        Rlp[] dependencyItems = [.. deps.Select(d => { byte[] b = new byte[Eip8288Constants.DependencyTripleLength]; d.WriteTo(b); return Rlp.Encode(b); })];
        Rlp[] proofItems = [.. deps.Select(_ => Rlp.Encode(new byte[] { 1 }))];
        return Rlp.Encode(Rlp.Encode([.. hashes.Select(h => Rlp.Encode(h))]), Rlp.Encode(MempoolWrapper.ModeDirect),
            Rlp.Encode(Rlp.Encode(dependencyItems), Rlp.Encode(proofItems))).Bytes;
    }

    private static byte[] Direct(IReadOnlyList<Transaction> txs, IReadOnlyList<FrameDependency> deps) =>
        Encode(txs.Select(t => t.Hash!).OrderBy(h => h.Bytes.ToArray(), Core.Extensions.Bytes.Comparer), deps);

    public static IEnumerable<TestCaseData> Cases()
    {
        Transaction a = Tx(0, First), b = Tx(1, Second), plain = Tx(1);
        Hash256[] sorted = [.. new[] { a.Hash!, b.Hash! }.OrderBy(h => h.Bytes.ToArray(), Core.Extensions.Bytes.Comparer)];
        List<FrameDependency> both = Eip8288Dependencies.Canonicalize([First, Second]);
        yield return Case("unsorted transaction hashes", Encode(sorted.Reverse(), both), [a, b], true, ProofWrapperAcceptanceStatus.Invalid, "RLP");
        yield return Case("duplicate transaction hash", Encode([a.Hash!, a.Hash!], [First]), [a], true, ProofWrapperAcceptanceStatus.Invalid, "RLP");
        yield return Case("empty dependencies", Encode([a.Hash!], []), [a], true, ProofWrapperAcceptanceStatus.Invalid, "RLP");
        yield return Case("a listed transaction without dependencies", Direct([a, plain], [First]), [a, plain], true,
            ProofWrapperAcceptanceStatus.Invalid, MempoolWrapperValidator.TransactionWithoutDependencies);
        yield return Case("deps beyond the transactions' union", Direct([a], both), [a], true, ProofWrapperAcceptanceStatus.Invalid, MempoolWrapperValidator.DepsMismatch);
        yield return Case("deps short of the transactions' union", Direct([a, b], [First]), [a, b], true, ProofWrapperAcceptanceStatus.Invalid, MempoolWrapperValidator.DepsMismatch);
        yield return Case("an invalid proof", Direct([a], [First]), [a], false, ProofWrapperAcceptanceStatus.Invalid, MempoolWrapperValidator.InvalidProof);
        yield return Case("an envelope that is not the listed transaction", Direct([a], [First]), [Tx(5, First)], true,
            ProofWrapperAcceptanceStatus.Invalid, "does not match");
        yield return Case("a missing envelope", Direct([a], [First]), [], true, ProofWrapperAcceptanceStatus.LocalFailure, MempoolWrapperValidator.UnknownTransaction);
    }

    private static TestCaseData Case(string name, byte[] wrapper, Transaction[] envelopes, bool proofsValid, ProofWrapperAcceptanceStatus status, string error) =>
        new TestCaseData(wrapper, envelopes, proofsValid, status, error).SetName($"Wrapper with {name} is {status}");

    [TestCaseSource(nameof(Cases))]
    public async Task Classifies_invalid_wrapper_shapes_as_the_spec_requires(byte[] wrapper, Transaction[] envelopes, bool proofsValid,
        ProofWrapperAcceptanceStatus status, string error)
    {
        ProofWrapperAcceptance result = await Service(proofsValid).AcceptDetailedAsync(wrapper,
            envelopes.ToDictionary(static tx => tx.CalculateHash().ValueHash256));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(status), result.Result.Error);
            Assert.That(result.Result.Error, Does.Contain(error));
            Assert.That(result.HasValidProof, Is.False, "nothing is admitted, announced or served");
        }
    }
}
