// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Encoding;

/// <summary>Covers the guest decoding of a transaction above the host's eager-hash size.</summary>
/// <remarks>
/// A host test process cannot hash in the guest build, so the test checks that decoding does not hash, and that the
/// delayed hash keeps the whole encoded transaction, which the first read of <see cref="Transaction.Hash"/> hashes.
/// </remarks>
public class GuestTxDelayedHashTests
{
    private const int DataLength = 40 * 1024;

    [Test]
    public void Large_transaction_keeps_its_bytes_for_the_delayed_hash(
        [Values(TxType.Legacy, TxType.EIP1559)] TxType type,
        [Values] bool memoryBacked)
    {
        Transaction tx = new()
        {
            Type = type,
            ChainId = 1,
            Nonce = 1,
            GasPrice = 2,
            DecodedMaxFeePerGas = 3,
            GasLimit = 10_000_000,
            To = new Address("0x00000000000000000000000000000000000000aa"),
            Value = 4,
            Data = new byte[DataLength],
            Signature = new Signature(1, 2, type == TxType.Legacy ? 37UL : 27UL)
        };
        byte[] bytes = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping);

        RlpReader reader = new(bytes);
        Transaction? decoded = null;
        TxDecoder.Instance.Decode(ref reader, ref decoded, RlpBehaviors.SkipTypedWrapping, memoryBacked ? bytes : default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bytes.Length, Is.GreaterThan(32 * 1024));
            Assert.That(decoded!.IsHashCalculated, Is.False);
            Assert.That(decoded.PreHash.ToArray(), Is.EqualTo(bytes));
        }
    }
}
