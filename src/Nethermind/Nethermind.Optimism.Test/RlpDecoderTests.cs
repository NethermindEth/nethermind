// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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

    // Derived with pyrlp: rlp(0x7e || rlp([sourceHash, from, to, mint, value, gas, isSystemTx, data])).
    // The independent pin catches a coordinated encode/decode field swap, which the roundtrip cannot see.
    private const string DepositTxRlpHex =
        "b8597ef856a003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760" +
        "94b7705ae4c6f81b66cdb323c65f4e8133690fc09994942921b14f1b1c385cd7e0cc2ef7abe5" +
        "598c835805038275300184deadbeef";

    [Test]
    public void Deposit_tx_fields_roundtrip()
    {
        Transaction tx = BuildDepositTx();

        byte[] rlp = new byte[_decoder.GetLength(tx, RlpBehaviors.None)];
        RlpWriter writer = new(rlp);
        _decoder.Encode(ref writer, tx);

        Assert.That(rlp.ToHexString(), Is.EqualTo(DepositTxRlpHex));

        RlpReader ctx = new(rlp);
        Transaction? decodedTx = _decoder.Decode(ref ctx);

        AssertDepositFieldsEqual(decodedTx, tx);
    }

    [Test]
    public void Deposit_tx_fields_roundtrip_through_Rlp()
    {
        Transaction tx = BuildDepositTx();

        byte[] rlp = new byte[_decoder.GetLength(tx, RlpBehaviors.None)];
        RlpWriter writer = new(rlp);
        _decoder.Encode(ref writer, tx);

        Transaction? decodedTx = Rlp.Decode<Transaction?>(rlp);

        AssertDepositFieldsEqual(decodedTx, tx);
    }

    [Test]
    public void Can_decode_Legacy_Empty_Signature()
    {
        // See: https://github.com/NethermindEth/nethermind/issues/7880
        string hexBytes =
            "f901c9830571188083030d4094420000000000000000000000000000000000000780b901a4cbd4ece9000000000000000000000000420000000000000000000000000000000000001000000000000000000000000099c9fc46f92e8a1c0dec1b1747d010903e884be10000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000005711800000000000000000000000000000000000000000000000000000000000000e4662a633a000000000000000000000000dac17f958d2ee523a2206206994597c13d831ec700000000000000000000000094b008aa00579c1307b0ef2c499ad98a8ce58e58000000000000000000000000117274dde02bc94006185af87d78beab28ceae06000000000000000000000000117274dde02bc94006185af87d78beab28ceae06000000000000000000000000000000000000000000000000000000000c3d8b8000000000000000000000000000000000000000000000000000000000000000c0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000808080";
        byte[] bytes = Bytes.FromHexString(hexBytes);
        RlpReader context = new(bytes);

        Transaction transaction = _decoder.Decode(ref context);

        // The expected values come from an independent pyrlp decode of hexBytes.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transaction.Type, Is.EqualTo(TxType.Legacy));
            Assert.That(transaction.Nonce, Is.EqualTo(356632UL));
            Assert.That(transaction.GasPrice, Is.EqualTo(UInt256.Zero));
            Assert.That(transaction.GasLimit, Is.EqualTo(200_000L));
            Assert.That(transaction.To, Is.EqualTo(new Address("0x4200000000000000000000000000000000000007")));
            Assert.That(transaction.Value, Is.EqualTo(UInt256.Zero));
            Assert.That(transaction.Data.Length, Is.EqualTo(420));
            // A pre-Bedrock unsigned tx has empty v, r and s. The decoder must return a null signature and must not throw.
            Assert.That(transaction.Signature, Is.Null);
        }
    }

    // Each wire field gets a distinct non-default value. This makes a dropped or swapped field fail the equality checks.
    private static Transaction BuildDepositTx()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSourceHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .WithValue(3)
            .WithGasLimit(30_000)
            .WithIsOPSystemTransaction(true)
            .WithData([0xde, 0xad, 0xbe, 0xef])
            .TestObject;
        tx.Mint = 5;
        return tx;
    }

    private static void AssertDepositFieldsEqual(Transaction? decoded, Transaction expected)
    {
        Assert.That(decoded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.SourceHash, Is.EqualTo(expected.SourceHash));
            Assert.That(decoded.SenderAddress, Is.EqualTo(expected.SenderAddress));
            Assert.That(decoded.To, Is.EqualTo(expected.To));
            Assert.That(decoded.Mint, Is.EqualTo(expected.Mint));
            Assert.That(decoded.Value, Is.EqualTo(expected.Value));
            Assert.That(decoded.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(decoded.IsOPSystemTransaction, Is.EqualTo(expected.IsOPSystemTransaction));
            Assert.That(decoded.Data.ToArray(), Is.EqualTo(expected.Data.ToArray()));
        }
    }
}
