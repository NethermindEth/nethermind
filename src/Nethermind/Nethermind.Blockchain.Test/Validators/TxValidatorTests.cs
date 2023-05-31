// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FluentAssertions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[TestFixture]
public class TxValidatorTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test, Timeout(Timeout.MaxTestTime)]
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

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Zero_r_is_not_valid()
    {
        byte[] sigData = new byte[65];
        // r is zero
        sigData[63] = 1; // correct s

        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
    }

    private static byte CalculateV() => (byte)EthereumEcdsa.CalculateV(TestBlockchainIds.ChainId);

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Zero_s_is_not_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        // s is zero

        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Bad_chain_id_is_not_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = (byte)(1 + CalculateV());
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeFalse();
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void No_chain_id_legacy_tx_is_valid()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
    }

    [Test, Timeout(Timeout.MaxTestTime)]
    public void Is_valid_with_valid_chain_id()
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = CalculateV();
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction.WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        txValidator.IsWellFormed(tx, MuirGlacier.Instance).Should().BeTrue();
    }

    [Timeout(Timeout.MaxTestTime)]
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
        txValidator.IsWellFormed(tx, releaseSpec).Should().Be(!validateChainId);
    }

    [Timeout(Timeout.MaxTestTime)]
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
                ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>())
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, eip2930 ? Berlin.Instance : MuirGlacier.Instance);
    }

    [Timeout(Timeout.MaxTestTime)]
    [TestCase(TxType.Legacy, true, false, ExpectedResult = true)]
    [TestCase(TxType.Legacy, false, false, ExpectedResult = true)]
    [TestCase(TxType.AccessList, false, false, ExpectedResult = false)]
    [TestCase(TxType.AccessList, true, false, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, true, false, ExpectedResult = false)]
    [TestCase(TxType.EIP1559, true, true, ExpectedResult = true)]
    [TestCase((TxType)100, true, false, ExpectedResult = false)]
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
            .WithAccessList(txType == TxType.AccessList || txType == TxType.EIP1559
                ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>())
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        IReleaseSpec releaseSpec = new ReleaseSpec() { IsEip2930Enabled = eip2930, IsEip1559Enabled = eip1559 };
        return txValidator.IsWellFormed(tx, releaseSpec);
    }

    [Timeout(Timeout.MaxTestTime)]
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
                ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>())
                : null)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Berlin.Instance);
    }

    [Timeout(Timeout.MaxTestTime)]
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
                ? new AccessList(new Dictionary<Address, IReadOnlySet<UInt256>>())
                : null)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithSignature(signature).TestObject;

        tx.Type = txType;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, London.Instance);
    }

    [Timeout(Timeout.MaxTestTime)]
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
        txValidator.IsWellFormed(tx, releaseSpec).Should().Be(expectedResult);
    }

    [Timeout(Timeout.MaxTestTime)]
    [TestCase(TxType.EIP1559, false, ExpectedResult = true)]
    [TestCase(TxType.EIP1559, true, ExpectedResult = false)]
    [TestCase(TxType.Blob, true, ExpectedResult = true)]
    public bool MaxFeePerDataGas_should_be_set_for_blob_tx_only(TxType txType, bool isMaxFeePerDataGasSet)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        TransactionBuilder<Transaction> txBuilder = Build.A.Transaction
            .WithType(txType)
            .WithTimestamp(ulong.MaxValue)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerDataGas(isMaxFeePerDataGasSet ? 1 : null)
            .WithBlobVersionedHashes(txType == TxType.Blob ? Eip4844Constants.MinBlobsPerTransaction : null)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithSignature(signature);

        Transaction tx = txBuilder.TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Cancun.Instance);
    }


    [TestCaseSource(nameof(BlobVersionedHashInvalidTestCases))]
    [TestCaseSource(nameof(BlobVersionedHashValidTestCases))]
    public bool BlobVersionedHash_should_be_correct(byte[] hash)
    {
        byte[] sigData = new byte[65];
        sigData[31] = 1; // correct r
        sigData[63] = 1; // correct s
        sigData[64] = 27; // correct v
        Signature signature = new(sigData);
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithTimestamp(ulong.MaxValue)
            .WithMaxFeePerGas(1)
            .WithMaxFeePerDataGas(1)
            .WithBlobVersionedHashes(new[] { hash })
            .WithChainId(TestBlockchainIds.ChainId)
            .WithSignature(signature).TestObject;

        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Cancun.Instance);
    }

    [TestCaseSource(nameof(ShardBlobTxIncorrectTransactions))]
    public bool ShardBlobTransaction_fields_should_be_verified(Transaction tx)
    {
        TxValidator txValidator = new(TestBlockchainIds.ChainId);
        return txValidator.IsWellFormed(tx, Cancun.Instance);
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
            yield return new TestCaseData(MakeArray(1, 1, 0))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(31, 1, 0))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(33, 1, 0))
            {
                TestName = "Correct version, incorrect length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, 0, 0))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1 - 1, 0))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeArray(32, KzgPolynomialCommitments.KzgBlobHashVersionV1 + 1, 0))
            {
                TestName = "Incorrect version, correct length",
                ExpectedResult = false
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
            KzgPolynomialCommitments.InitializeAsync().Wait();
            static TransactionBuilder<Transaction> MakeTestObject(int blobCount = 1) => Build.A.Transaction
                .WithChainId(TestBlockchainIds.ChainId)
                .WithTimestamp(ulong.MaxValue)
                .WithMaxFeePerGas(1)
                .WithMaxFeePerDataGas(1)
                .WithShardBlobTxTypeAndFields(blobCount);

            yield return new TestCaseData(MakeTestObject().SignedAndResolved().TestObject)
            {
                TestName = "A correct shard blob tx",
                ExpectedResult = true
            };

            yield return new TestCaseData(MakeTestObject(0)
                .SignedAndResolved().TestObject)
            {
                TestName = "BlobVersionedHashes are empty",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MinBlobsPerTransaction - 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Not enough BlobVersionedHashes",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MinBlobsPerTransaction)
                .SignedAndResolved().TestObject)
            {
                TestName = "Bare minimum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MinBlobsPerTransaction + 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "More than minimum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MaxBlobsPerTransaction - 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Less than maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MaxBlobsPerTransaction)
                .SignedAndResolved().TestObject)
            {
                TestName = "Maximum BlobVersionedHashes",
                ExpectedResult = true
            };
            yield return new TestCaseData(MakeTestObject(Eip4844Constants.MaxBlobsPerTransaction + 1)
                .SignedAndResolved().TestObject)
            {
                TestName = "Too many BlobVersionedHashes",
                ExpectedResult = false
            };

            yield return new TestCaseData(MakeTestObject()
                .WithBlobVersionedHashes(new byte[][] { MakeArray(31, KzgPolynomialCommitments.KzgBlobHashVersionV1) })
                .SignedAndResolved().TestObject)
            {
                TestName = "BlobVersionedHashes are of a wrong length",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject()
                .With(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Blobs = Array.Empty<byte[]>())
                .SignedAndResolved().TestObject)
            {
                TestName = "Blobs count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject()
                .With(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Commitments = Array.Empty<byte[]>())
                .SignedAndResolved().TestObject)
            {
                TestName = "Commitments count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject()
                .With(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Proofs = Array.Empty<byte[]>())
                .SignedAndResolved().TestObject)
            {
                TestName = "Proofs count does not match hashes count",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject()
                .With(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Commitments[0][1] ^= 0xFF)
                .SignedAndResolved().TestObject)
            {
                TestName = "A commitment does not math hash",
                ExpectedResult = false
            };
            yield return new TestCaseData(MakeTestObject()
                .With(tx => ((ShardBlobNetworkWrapper)tx.NetworkWrapper!).Proofs[0][1] ^= 0xFF)
                .SignedAndResolved().TestObject)
            {
                TestName = "Proofs are not valid",
                ExpectedResult = false
            };
        }
    }
}
