// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CkzgLib;
using FluentAssertions;
using Nethermind.Consensus.Messages;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class TxValidatorTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Curve_is_correct()
    {
        BigInteger N =
            BigInteger.Parse("115792089237316195423570985008687907852837564279074904382605163141518161494337");
        BigInteger HalfN = N / 2;

        Secp256K1Curve.N.Convert(out BigInteger n);
        Secp256K1Curve.HalfN.Convert(out BigInteger halfN);

        (N == n).Should().BeTrue();
        (HalfN == halfN).Should().BeTrue();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Zero_r_is_not_valid()
    {
        byte[] sigData = new byte[65];
        // r is zero
        sigData[63] = 1; // correct s

        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).AsBool().Should().BeFalse();
    }

    private static byte CalculateV() => (byte)EthereumEcdsaExtensions.CalculateV(TestBlockchainIds.ChainId);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Zero_s_is_not_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        // s is zero

        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).AsBool().Should().BeFalse();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Bad_chain_id_is_not_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = (byte)(1 + CalculateV());
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).AsBool().Should().BeFalse();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void No_chain_id_legacy_tx_is_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).AsBool().Should().BeTrue();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Is_valid_with_valid_chain_id()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = CalculateV();
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).AsBool().Should().BeTrue();
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(true)]
    [TestCase(false)]
    public void Before_eip_155_has_to_have_valid_chain_id_unless_overridden(bool validateChainId)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 41;
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        IReleaseSpec releaseSpec = Substitute.For<IReleaseSpec>();
        releaseSpec.IsEip155Enabled.Returns(false);
        releaseSpec.ValidateChainId.Returns(validateChainId);

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, releaseSpec).AsBool().Should().Be(!validateChainId);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(TxType.Legacy, true, ExpectedResult = true)]
    [TestCase(TxType.Legacy, false, ExpectedResult = true)]
    [TestCase(TxType.AccessList, false, ExpectedResult = false)]
    [TestCase(TxType.AccessList, true, ExpectedResult = true)]
    [TestCase((TxType)100, true, ExpectedResult = false)]
    public bool Before_eip_2930_has_to_be_legacy_tx(TxType txType, bool eip2930)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithAccessList(txType == TxType.AccessList
                ? AccessList.Empty
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, eip2930 ? Berlin.Instance : MuirGlacier.Instance);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(TxType.Legacy, true, false, ExpectedResult = true)]
    [TestCase(TxType.Legacy, false, false, ExpectedResult = true)]
    [TestCase(TxType.AccessList, false, false, ExpectedResult = false)]
    [TestCase(TxType.AccessList, true, false, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, true, false, ExpectedResult = false)]
    [TestCase(TxType.EIP1559, true, true, ExpectedResult = true)]
    public bool Before_eip_1559_has_to_be_legacy_or_access_list_tx(TxType txType, bool eip2930, bool eip1559)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = CalculateV();
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithType(txType)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithMaxPriorityFeePerGas(txType == TxType.EIP1559 ? 10.GWei() : 5.GWei())
            .WithMaxFeePerGas(txType == TxType.EIP1559 ? 10.GWei() : 5.GWei())
            .WithAccessList(txType is TxType.AccessList or TxType.EIP1559
                ? AccessList.Empty
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        IReleaseSpec releaseSpec = new ReleaseSpec() { IsEip2930Enabled = eip2930, IsEip1559Enabled = eip1559 };
        return txValidator.IsWellFormed(tx, releaseSpec);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(TxType.Legacy, ExpectedResult = true)]
    [TestCase(TxType.AccessList, ExpectedResult = false)]
    [TestCase(TxType.EIP1559, ExpectedResult = false)]
    public bool Chain_Id_required_for_non_legacy_transactions_after_Berlin(TxType txType)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = CalculateV();
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
            .WithAccessList(txType == TxType.AccessList
                ? AccessList.Empty
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Berlin.Instance);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(TxType.Legacy, 10, 5, ExpectedResult = true)]
    [TestCase(TxType.AccessList, 10, 5, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, 10, 5, ExpectedResult = true)]
    [TestCase(TxType.Legacy, 5, 10, ExpectedResult = true)]
    [TestCase(TxType.AccessList, 5, 10, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, 5, 10, ExpectedResult = false)]
    public bool MaxFeePerGas_is_required_to_be_greater_than_MaxPriorityFeePerGas(TxType txType, int maxFeePerGas,
        int maxPriorityFeePerGas)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithType(txType > TxType.AccessList ? TxType.Legacy : txType)
            .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
            .WithMaxFeePerGas((UInt256)maxFeePerGas)
            .WithAccessList(txType == TxType.AccessList
                ? AccessList.Empty
                : null)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, London.Instance);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(true, 1, false)]
    [TestCase(false, 1, true)]
    [TestCase(true, -1, true)]
    [TestCase(false, -1, true)]
    public void Transaction_with_init_code_above_max_value_is_rejected_when_eip3860Enabled(bool eip3860Enabled,
        int dataSizeAboveInitCode, bool expectedResult)
    {
        IReleaseSpec releaseSpec = eip3860Enabled ? Shanghai.Instance : GrayGlacier.Instance;
        byte[] initCode = Enumerable.Repeat((byte)0x20, (int)releaseSpec.MaxInitCodeSize + dataSizeAboveInitCode)
            .ToArray();
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27;
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithSignature(signature)
            .WithGasLimit(int.MaxValue)
            .WithChainId(TestBlockchainIds.ChainId)
            .To(null)
            .WithData(initCode).TestObject;

        TxValidator txValidator = new(1);
        txValidator.IsWellFormed(tx, releaseSpec).AsBool().Should().Be(expectedResult);
    }

    //leading zeros in AccessList - expected to pass (real mainnet tx)
    [TestCase("02f9036b018293308501927cc03e85068c60a3fe830255ac94007933790a4f00000099e9001629d9fe7775b80085cf055a432fb549000000000000010304e7fd24a76c92f89ba376dc84a95b2d16c601b0cd460cd5fc1406b4dca60665ed17fa81862fb9b9466cff35f902c0f901c594fc1406b4dca60665ed17fa81862fb9b9466cff35f901ada00000000000000000000000000000000000000000000000000000000000000000a0a21e3a85ad298ca52cbd0141d1ff6668b7ad4d00ffe9c475cd979b8808f291a7a0c99376fdf1e175eca34a13a76dc93667c6e9af348b4ea52bebae6b6ae1ca90f2a00000000000000000000000000000000000000000000000000000000000000010a0000000000000000000000000000000000000000000000000000000000000000fa00000000000000000000000000000000000000000000000000000000000000013a069aee5143042beb78546d9399b2cf98756e3a817759ed26e15f49f1d6b10d2cfa00000000000000000000000000000000000000000000000000000000000000008a0000000000000000000000000000000000000000000000000000000000000000ca0000000000000000000000000000000000000000000000000000000000000000da04f92411ced6240982b50672fc4005c2931fc1dc1073611fa42e1f1bc5e2406dda0c10f083d7c68a8a250bebffca3bd7be41dff318f4928f403c94fea79cae02739a00000000000000000000000000000000000000000000000000000000000000009f89b94a76c92f89ba376dc84a95b2d16c601b0cd460cd5f884a0000000000000000000000000000000000000000000000000000000000000000ca00000000000000000000000000000000000000000000000000000000000000008a00000000000000000000000000000000000000000000000000000000000000006a00000000000000000000000000000000000000000000000000000000000000007f85994c02aaa39b223fe8d0a0e5c4f27ead9083c756cc2f842a06f87cb8c221d0dc2fdefea9c226dcfaa1715b94c1ac546879b8e3716c44dcfa5a05b1fd252f972f66f1de33cbc3b44509a4ec1fc4dee11857461b8b9131f605c4201a0fbb2bc129106bf11dc928370891677969c3d3f1ceb845083c61d26fb2c4f30caa07d966ee2630215c2c059536ac0e0df08a551abe9c4477d098b7723c54bb2fdb8", ExpectedResult = true)]
    //leading zeros in Uint256
    [TestCase("0xf863803083303030941000000030301a9e30303030303030303030303083000030301ba03030309430303030303030303030303030303030303030303030303030303030a03030303030303030303030303030303030303030303030303030303030303030", ExpectedResult = false)]
    [TestCase("0xf863000083303030943030301e3030303030303030303030303030303083303030301ba03030303030303030303030303030303030303030303030303030303030303030a03030303030303030303030303030303030303030301030303030303030303030", ExpectedResult = false)]
    //leading zeros in Long
    [TestCase("0xf86380308300643094303030303030303030303030303030303030303083303030301ba03030303030303030303030303030303030303030303030303030303030303030a03030303030303030303030303030303030303030303030303030303030303030", ExpectedResult = false)]
    //prefixes
    [TestCase("0x01f87b018001826a4094095e7baea6a6c7c4c2dfeb977efac326af552d878080dad994a95e7baea6a6c7c4c2dfeb977efac326af552d87c382000180a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c0706ebfa6d06e3f4491dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0x01f89a018001826a4094095e7baea6a6c7c4c2dfeb977efac326af552d878080f838e894a95e7baea6a6c7c4c2dfeb977efac326af552d87e1a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c0706ebfa6d06e3f4491dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0x01f89a018001826a4094097e7baea6a6c7c4c2dfeb977efac326af552d878080f838f794a95e7baea6a6c7c4c2dfeb977efac326af552d87e1a0fffffffffffffffffffffffffffffffcffffffffffffffffffffffffff00008000a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c0706ebfa6d06e3f4371dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0x01f89a018001826a4094095e7baea6a6c7c4c2dfeb977efac326af552d878080f838f794a95e7baea6a6c7c4c2dfeb977efac326af552d87a1a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c0706ebfa6d06e3f4491dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0x01f89a018001826a4094095e7baea6a6c7c4c2dfeb977efac326af552d878080b838f794a95e7baea6a6c7c4c2dfeb977efac326af552d87e1a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c0706ebfa6d06e3f4491dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0x01f89a018001826a4094095e7baea6a6c7c406ebfa6d06e3f426af552d878080b838f794a95e7baea6a6c7c4c2dfeb977efac326af552d87e1a0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff80a05cbd172231fc0735e0fb994dd5b1a4939170a260b36f0427a8a80866b063b948a07c230f7f578dd61785c93361b9871c07c2dfeb977efac3491dc9558c5202ed36", ExpectedResult = false)]
    [TestCase("0xb863303083303030943030303030303030303030303030303030303030833030303025a0ffffffffffffffff303030303030303030303030303030303030303030303030a03030308330303030303030303030303030303030303030303030303030303030", ExpectedResult = false)]
    //additional bytes
    [TestCase("0xf85f030182520894b94f5374fce5edbc8e2a8697c15331677e6ebf0b0a801ca098ff921201554726367d2be8c804a7ff898af285ebc57dffcce4c44b9c19ac4aa01887321be536891275ec75c8095f789dd4c743dfe42c1820f9231f98a962b210e3ac2452a3", ExpectedResult = false)]
    [TestCase("0xf85f011082520894f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f001801ca06f0010ff4c31c2a6d0526c0d414e6cd01ad5d22e15bfff98af23867366b94d87a05413392d556119132da7056f8fb56a913848484848484848484848484848484848", ExpectedResult = false)]
    [TestCase("0xf867078504a817c807830290409435353535353535353535353535353535353535358201578025a052f1a9b320cab38e5da8a8f97989383aab0a49165fc91c737310e4f7e9821021a044444452f1a9b320cab34e4ea8a8f97989383aab0a49165fc91c737310e4f7e9821021", ExpectedResult = false)]
    [TestCase("0xf85f011082520894f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f001801ca06f0010ff4c31c2a6d0526c0d414e6cd01ad5d22e15bfff98af23867366b94d87a05413392d556119132da7056f8fb56a9138a36446a8a4ad7159c9d866b94d8792d9f32284", ExpectedResult = false)]
    [TestCase("0xf863803083303030943030303030303030303030303030303030302f3083303030301ba03030303030303030303030303030303030303030303030303030303030303030a030303030303030303030f25fab96b8e4b8c530303030303030303030303030303030303030303030", ExpectedResult = false)]
    //invalid V
    [TestCase("0xf85f303082d630943030303030303030303030303030303030303030303080a03030303030303030303030303030303030303030303030303030303030303030a03030303030303030303030303030303030303030303030303030303030303030", ExpectedResult = false)]
    [TestCase("0xd9303086303030303030803030808a3030303030303030303030", ExpectedResult = false)]
    [TestCase("0xf8631b3083303030943030303030303030303030303030303030303030833030303080a03030303030303030303030303030303030303030303030303030303030303030a03030303030303030303030303030303030303030303030303030303030303030", ExpectedResult = false)]
    [TestCase("0xf85f011082520894f080fffffff0f0f0f0f0f0f0f0f0f0f0f0f0f0f0018004a06f0010ff4c31c2a6d0526c0d414e6cd01ad5d22e15bfff98af23867366b94d87a05413392d556119132da7056f8fb56a9138a36446a8a4ad7159c9d892d9f32284", ExpectedResult = false)]
    //wrong V
    [TestCase("0xf863303083303030943030303030303030303030303030303030303030833030303020a0ffffffffffffffffffbfffffffffffffffffffffffffffffffffffffffffffffa040df00d70ec28c94a3b55ec771bcbc70778d6ee0b51ca7ea9514594c861b1884", ExpectedResult = false)]
    [TestCase("0xf863303083303030943030303030303030303030303030303030303030833030303022a0ffffffffffffffffffbfffffffffffffffffffffffffffffffffffffffffffffa040df00d70ec28c94a3b55ec771bcbc70778d6ee0b51ca7ea9514594c861b1884", ExpectedResult = false)]
    public bool Incorrect_transactions_are_rejected(string rlp)
    {
        try
        {
            Transaction tx = Rlp.Decode<Transaction>(Bytes.FromHexString(rlp), RlpBehaviors.SkipTypedWrapping);
            TxValidator txValidator = new(BlockchainIds.Mainnet);
            return txValidator.IsWellFormed(tx, London.Instance);
        }
        catch (Exception e) when (e is RlpException or ArgumentException)
        {
            return false; // RLP or Argument Exception means that we rejected transaction
        }
    }

    [Test]
    public void ShardBlobTransactions_should_have_destination_set()
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Transaction txWithoutTo = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithTimestamp(ulong.MaxValue)
            .WithTo(null)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerBlobGas(1)
            .WithBlobVersionedHashes(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved().TestObject;

        Transaction txWithTo = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithTimestamp(ulong.MaxValue)
            .WithTo(TestItem.AddressA)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerBlobGas(1)
            .WithBlobVersionedHashes(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved().TestObject;

        Assert.That(txValidator.IsWellFormed(txWithoutTo, Cancun.Instance).AsBool(), Is.False);
        Assert.That(txValidator.IsWellFormed(txWithTo, Cancun.Instance).AsBool());
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(TxType.EIP1559, false, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, true, ExpectedResult = false)]
    [TestCase(TxType.Blob, true, ExpectedResult = true)]
    public bool MaxFeePerBlobGas_should_be_set_for_blob_tx_only(TxType txType, bool isMaxFeePerBlobGasSet)
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(txType)
            .WithTimestamp(ulong.MaxValue)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerBlobGas(isMaxFeePerBlobGasSet ? 1 : null)
            .WithBlobVersionedHashes(txType == TxType.Blob ? Eip4844Constants.MinBlobsPerTransaction : null)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Cancun.Instance);
    }

    [TestCaseSource(nameof(BlobVersionedHashInvalidTestCases))]
    [TestCaseSource(nameof(BlobVersionedHashValidTestCases))]
    public bool BlobVersionedHash_should_be_correct(byte[] hash)
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithTimestamp(ulong.MaxValue)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerBlobGas(1)
            .WithBlobVersionedHashes(new[] { hash })
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved().TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Cancun.Instance);
    }

    [TestCaseSource(nameof(ShardBlobTxIncorrectTransactions))]
    public bool ShardBlobTransaction_fields_should_be_verified(IReleaseSpec spec, Transaction tx)
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, spec);
    }

    [Test]
    public void IsWellFormed_NotBlobTxButMaxFeePerBlobGasIsSet_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithMaxFeePerBlobGas(1)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_NotBlobTxButBlobVersionedHashesIsSet_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithBlobVersionedHashes([[0x0]])
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_BlobTxToIsNull_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithTo(null)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }
    [Test]
    public void IsWellFormed_BlobTxHasMoreDataGasThanAllowed_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithBlobVersionedHashes(100)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_BlobTxHasNoBlobs_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithBlobVersionedHashes(0)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_BlobTxHasBlobOverTheSizeLimit_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Blobs[0] = new byte[Ckzg.BytesPerBlob + 1];
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_BlobTxHasCommitmentOverTheSizeLimit_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Commitments[0] = new byte[Ckzg.BytesPerCommitment + 1];
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_BlobTxHasProofOverTheSizeLimit_ReturnFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Proofs[0] = new byte[Ckzg.BytesPerProof + 1];
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Cancun.Instance).AsBool(), Is.False);
    }

    [Test]
    public void IsWellFormed_CreateTxInSetCode_ReturnsFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithAuthorizationCode(new AuthorizationTuple(0, TestItem.AddressA, 0, 0, 0, 0))
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .WithTo(null);

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.Multiple(() =>
        {
            ValidationResult validationResult = txValidator.IsWellFormed(tx, Prague.Instance);
            Assert.That(validationResult.AsBool(), Is.False);
            Assert.That(validationResult.Error, Is.EqualTo(TxErrorMessages.NotAllowedCreateTransaction));
        });
    }

    [Test]
    public void IsWellFormed_AuthorizationListTxInPragueSpec_ReturnsTrue()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressA)
            .WithAuthorizationCode(new AuthorizationTuple(0, TestItem.AddressA, 0, 0, 0, 0))
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Prague.Instance).AsBool, Is.True);
    }

    [Test]
    public void IsWellFormed_EmptyAuthorizationList_ReturnsFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressA)
            .WithAuthorizationCode([])
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Prague.Instance).AsBool, Is.False);
    }

    [Test]
    public void IsWellFormed_NullAuthorizationList_ReturnsFalse()
    {
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressA)
            .WithAuthorizationCode((AuthorizationTuple[])null!)
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Prague.Instance).AsBool, Is.False);
    }

    private static IEnumerable<TxType> NonSetCodeTypes() =>
        Enum.GetValues<TxType>().Where(static t => t != TxType.SetCode && t != TxType.DepositTx);

    [TestCaseSource(nameof(NonSetCodeTypes))]
    public void IsWellFormed_NonSetCodeTxHasAuthorizationList_ReturnsFalse(TxType type)
    {
        var x = Enum.GetValues<TxType>().Where(static t => t != TxType.SetCode);
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(type)
            .WithTo(TestItem.AddressA)
            .WithMaxFeePerGas(100000)
            .WithGasLimit(1000000)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithShardBlobTxTypeAndFieldsIfBlobTx()
            .WithAuthorizationCode(new AuthorizationTuple(TestBlockchainIds.ChainId, TestItem.AddressA, 0, new Signature(new byte[65])))
            .SignedAndResolved();

        Transaction tx = txBuilder.TestObject;
        TxValidator txValidator = new(TestBlockchainIds.ChainId);

        Assert.That(txValidator.IsWellFormed(tx, Prague.Instance).Error, Is.EqualTo(TxErrorMessages.NotAllowedAuthorizationList));
    }

    [Test]
    public void IsWellFormed_TransactionWithGasLimitExceedingEip7825Cap_ReturnsFalse()
    {
        Transaction tx = Build.A.Transaction
            .WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + 1)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved().TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        // todo: change to osaka
        IReleaseSpec releaseSpec = new ReleaseSpec() { IsEip7825Enabled = true };
        ValidationResult result = txValidator.IsWellFormed(tx, releaseSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AsBool, Is.False);
            Assert.That(result.Error, Is.EqualTo(TxErrorMessages.TxGasLimitCapExceeded(tx.GasLimit, Eip7825Constants.DefaultTxGasLimitCap)));
        }
    }

    private static byte[] MakeArray(int count, params byte[] elements) =>
        elements.Take(Math.Min(count, elements.Length))
            .Concat(new byte[Math.Max(0, count - elements.Length)])
            .ToArray();

    private static IEnumerable<TestCaseData> BlobVersionedHashInvalidTestCases
    {
        get
        {
            yield return new TestCaseData(null) { TestName = "Null hash", ExpectedResult = false };
            yield return new TestCaseData(MakeArray(0)) { TestName = "Empty hash", ExpectedResult = false };
            yield return new TestCaseData(MakeArray(1, 1))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(31, 1))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(33, 1))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, 0))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1 - 1))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1 + 1))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1))
            {
                TestName = "Correct version, correct length",
                ExpectedResult = true
            };
        }
    }

    private static IEnumerable<TestCaseData> BlobVersionedHashValidTestCases
    {
        get
        {
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1, 0))
            {
                TestName = "Correct version, correct length",
                ExpectedResult = true
            };
        }
    }

    private static IEnumerable<TestCaseData> ShardBlobTxIncorrectTransactions
    {
        get
        {
            static TransactionBuilder<Transaction> MakeTestObject(int blobCount = 1) => Build.A.Transaction
                .WithChainId(TestBlockchainIds.ChainId)
                .WithTimestamp(ulong.MaxValue)
                .WithMaxFeePerGas(1)
                .WithMaxFeePerBlobGas(1)
                .WithShardBlobTxTypeAndFields(blobCount);

            yield return new TestCaseData(Cancun.Instance, MakeTestObject().SignedAndResolved().TestObject)
            {
                TestName = "A correct shard blob tx",
                ExpectedResult = true
            };

            yield return new TestCaseData(Cancun.Instance, MakeTestObject(0)
                .SignedAndResolved().TestObject)
            {
                TestName = "BlobVersionedHashes are empty",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject(Eip4844Constants.MinBlobsPerTransaction - 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Not enough BlobVersionedHashes",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject(Eip4844Constants.MinBlobsPerTransaction)
                .SignedAndResolved().TestObject)
            {
                TestName = "Bare minimum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject(Eip4844Constants.MinBlobsPerTransaction + 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "More than minimum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject((int)Cancun.Instance.MaxBlobCount - 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Less than maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject((int)Cancun.Instance.MaxBlobCount)
                .SignedAndResolved().TestObject)
            {
                TestName = "Maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject((int)Cancun.Instance.MaxBlobCount + 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Too many BlobVersionedHashes",
                ExpectedResult = false
            };

            yield return new TestCaseData(Prague.Instance, MakeTestObject((int)Prague.Instance.MaxBlobCount - 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Less than maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Prague.Instance, MakeTestObject((int)Prague.Instance.MaxBlobCount)
                .SignedAndResolved().TestObject)
            {
                TestName = "Maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(Prague.Instance, MakeTestObject((int)Prague.Instance.MaxBlobCount + 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Too many BlobVersionedHashes",
                ExpectedResult = false
            };

            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .WithBlobVersionedHashes(new byte[][] { MakeArray(31, KzgPolynomialCommitments.KzgBlobHashVersionV1) })
                .SignedAndResolved().TestObject)
            {
                TestName = "BlobVersionedHashes are of a wrong length",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .With(static tx => tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { Blobs = [] })
                .SignedAndResolved().TestObject)
            {
                TestName = "Blobs count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .With(static tx => tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { Commitments = [] })
                .SignedAndResolved().TestObject)
            {
                TestName = "Commitments count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .With(static tx => tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { Proofs = [] })
                .SignedAndResolved().TestObject)
            {
                TestName = "Proofs count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .With(static tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Commitments[0][1] ^= 0xFF)
                .SignedAndResolved().TestObject)
            {
                TestName = "A commitment does not math hash",
                ExpectedResult = false
            };
            yield return new TestCaseData(Cancun.Instance, MakeTestObject()
                .With(static tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Proofs[0][1] ^= 0xFF)
                .SignedAndResolved().TestObject)
            {
                TestName = "Proofs are not valid",
                ExpectedResult = false
            };
        }
    }
}
