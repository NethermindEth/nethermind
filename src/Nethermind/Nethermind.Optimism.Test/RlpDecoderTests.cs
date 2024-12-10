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
    private TxDecoder _decoder = null!;

    [SetUp]
    public void Setup()
    {
        _decoder = TxDecoder.Instance;
        _decoder.RegisterDecoder(new OptimismTxDecoder<Transaction>());
    }

    [Test]
    public void Can_decode_non_null_Transaction()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).TestObject;

        RlpStream rlpStream = new(_decoder.GetLength(tx, RlpBehaviors.None));
        _decoder.Encode(rlpStream, tx);
        rlpStream.Reset();

        Transaction? decodedTx = _decoder.Decode(rlpStream);

        decodedTx.Should().NotBeNull();
    }

    [Test]
    public void Can_decode_non_null_Transaction_through_Rlp()
    {
        _decoder.RegisterDecoder(new OptimismTxDecoder<Transaction>());

        Transaction tx = Build.A.Transaction.WithType(TxType.DepositTx).TestObject;

        RlpStream rlpStream = new(_decoder.GetLength(tx, RlpBehaviors.None));
        _decoder.Encode(rlpStream, tx);
        rlpStream.Reset();

        Transaction? decodedTx = Rlp.Decode<Transaction?>(rlpStream);

        decodedTx.Should().NotBeNull();
    }
}
