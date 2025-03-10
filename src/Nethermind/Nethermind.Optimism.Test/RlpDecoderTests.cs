// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
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
        _decoder.RegisterDecoder(new OptimismLegacyTxDecoder());
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

    [Test]
    public void Can_decode_Legacy_Empty_Signature()
    {
        _decoder.RegisterDecoder(new OptimismTxDecoder<Transaction>());

        // See: https://github.com/NethermindEth/nethermind/issues/7880
        var hexBytes =
            "f901c9830571188083030d4094420000000000000000000000000000000000000780b901a4cbd4ece9000000000000000000000000420000000000000000000000000000000000001000000000000000000000000099c9fc46f92e8a1c0dec1b1747d010903e884be10000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000005711800000000000000000000000000000000000000000000000000000000000000e4662a633a000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec700000000000000000000000094b008aa00579c1307b0ef2c499ad98a8ce58e58000000000000000000000000117274dde02bc94006185af87d78beab28ceae06000000000000000000000000117274dde02bc94006185af87d78beab28ceae06000000000000000000000000000000000000000000000000000000000c3d8b8000000000000000000000000000000000000000000000000000000000000000c0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000808080";
        var bytes = Bytes.FromHexString(hexBytes);
        var context = bytes.AsRlpValueContext();

        var transaction = _decoder.Decode(ref context);

        transaction.Should().NotBeNull();
    }
}
