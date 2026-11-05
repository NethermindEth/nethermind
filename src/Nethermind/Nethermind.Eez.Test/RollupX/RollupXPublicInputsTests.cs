// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXPublicInputsTests
{
    private const ulong RollupId = 1;

    /// <summary>The eez.dev proof system and the signer its verification key names.</summary>
    internal static readonly Address ProofSystem = new("0xff0cf5519b755f569f39a4b1ee09bedc984a5b10");
    internal static readonly Address OperatorSigner = new("0x72a5ed7ab535f5760a77d1770a49cf212e20bc3b");

    /// <summary>
    /// The batches the eez.dev testnet settled: each carries the operator signer's signature over its public inputs hash,
    /// so the hash computed from the batch, its blobs and the L1 block it landed in must recover to that signer.
    /// </summary>
    [Test]
    public void PublicInputsHash_RecordedTestnetBatches_RecoversTheOperatorSigner()
    {
        using JsonDocument replay = JsonDocument.Parse(File.ReadAllText(StatelessFixtures.PathOf("rollupx/eezdev-699069", "replay-0-2250.json")));
        ValueHash256 key = VerificationKey(OperatorSigner);
        EthereumEcdsa ecdsa = new(0);
        int checkedBatches = 0;
        foreach (JsonElement recorded in replay.RootElement.GetProperty("batches").EnumerateArray())
        {
            JsonElement transaction = recorded.GetProperty("transaction");
            JsonElement l1Block = recorded.GetProperty("l1Block");
            PostBatch batch = EezCalldata.DecodePostAndVerifyBatch(Bytes.FromHexString(transaction.GetProperty("input").GetString()!));
            Hash256[] blobs = [.. transaction.GetProperty("blobVersionedHashes").EnumerateArray().Select(static h => new Hash256(h.GetString()!))];
            L1PostingTarget target = new(Bytes.FromHexString(l1Block.GetProperty("timestamp").GetString()!).ToULongFromBigEndianByteArrayWithoutLeadingZeros(),
                new Hash256(l1Block.GetProperty("parentHash").GetString()!));

            Assert.That(() => RollupXPostBatchProfile.Validate(batch, RollupId, ProofSystem, blobs.Length), Throws.Nothing, "the operator signs only the profile");
            ValueHash256 hash = RollupXPostBatchProfile.PublicInputsHash(batch, RollupId, key, blobs, target);
            Assert.That(RollupXPostBatchProfile.PublicInputsHash(batch, RollupId, key, blobs,
                target with { Timestamp = target.Timestamp + 1 }), Is.Not.EqualTo(hash), "the posting timestamp is bound");
            byte[] proof = batch.Proofs.Single();
            Assert.That(ecdsa.RecoverAddress(new Signature(proof.AsSpan(0, 64), proof[64] - 27), in hash), Is.EqualTo(OperatorSigner),
                $"the batch in L1 block {l1Block.GetProperty("number").GetString()} is signed over this hash");
            checkedBatches++;
        }

        Assert.That(checkedBatches, Is.EqualTo(14));
    }

    internal static ValueHash256 VerificationKey(Address signer)
    {
        byte[] key = new byte[32];
        signer.Bytes.CopyTo(key.AsSpan(12));
        return new ValueHash256(key);
    }
}
