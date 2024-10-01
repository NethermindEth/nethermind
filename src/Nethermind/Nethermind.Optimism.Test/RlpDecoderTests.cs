// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class RlpDecoderTests
{
    [Test]
    public void Can_decode_non_null_Transaction()
    {
        TxDecoder decoder = TxDecoder.Instance;
        decoder.RegisterDecoder(new OptimismTxDecoder<Transaction>());

        Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).TestObject;

        RlpStream rlpStream = new(decoder.GetLength(tx, RlpBehaviors.None));
        decoder.Encode(rlpStream, tx);
        rlpStream.Reset();

        Transaction? decodedTx = decoder.Decode(rlpStream);

        decodedTx.Should().NotBeNull();
    }

    [Test]
    public void Can_decode_non_null_Transaction_through_Rlp()
    {
        TxDecoder decoder = TxDecoder.Instance;
        decoder.RegisterDecoder(new OptimismTxDecoder<Transaction>());

        Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).TestObject;

        RlpStream rlpStream = new(decoder.GetLength(tx, RlpBehaviors.None));
        decoder.Encode(rlpStream, tx);
        rlpStream.Reset();

        Transaction? decodedTx = Rlp.Decode<Transaction?>(rlpStream);

        decodedTx.Should().NotBeNull();
    }
}
