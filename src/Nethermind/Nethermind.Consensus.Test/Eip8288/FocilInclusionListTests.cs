// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

public class FocilInclusionListTests
{
    private static readonly ILeanProofVerifier Accepting = new FakeLeanProofVerifier(true);

    private static Transaction DepTx(byte scheme)
    {
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = scheme;
        return new Transaction
        {
            Type = TxType.FrameTx,
            Frames = [new TxFrame(TxFrame.ModeDepVerify, 0, null, 0, UInt256.Zero, data)],
        };
    }

    [Test]
    public void Empty_focil_round_trips()
    {
        FocilInclusionList focil = new()
        {
            Transactions = [],
            RecursiveStark = new RecursiveStark([1, 2, 3], Keccak.Compute("deps")),
        };

        Rlp rlp = FocilInclusionListDecoder.Instance.Encode(focil);
        RlpReader reader = new(rlp.Bytes);
        FocilInclusionList decoded = FocilInclusionListDecoder.Instance.Decode(ref reader);

        Assert.That(decoded.Transactions.Count, Is.EqualTo(0));
        Assert.That(decoded.RecursiveStark.StarkProof, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(decoded.RecursiveStark.BlockDepsHash, Is.EqualTo(Keccak.Compute("deps")));
    }

    [Test]
    public void Valid_when_recursive_stark_commits_to_all_dependencies()
    {
        List<Transaction> txs = [DepTx(Eip8288Constants.LeanSphincsScheme), DepTx(Eip8288Constants.LeanStarkScheme)];
        List<FrameDependency> deps = [];
        foreach (Transaction tx in txs) deps.AddRange(Eip8288Dependencies.ForTransaction(tx));

        FocilInclusionList focil = new()
        {
            Transactions = txs,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps))),
        };

        Assert.That(FocilInclusionListValidator.Validate(focil, Accepting, out string? error), Is.True, error);
    }

    [Test]
    public void Rejects_deps_hash_mismatch()
    {
        FocilInclusionList focil = new()
        {
            Transactions = [DepTx(Eip8288Constants.LeanSphincsScheme)],
            RecursiveStark = new RecursiveStark([1], Keccak.Compute("wrong")),
        };

        Assert.That(FocilInclusionListValidator.Validate(focil, Accepting, out string? error), Is.False);
        Assert.That(error, Is.EqualTo(FocilInclusionListValidator.DepsHashMismatch));
    }
}
