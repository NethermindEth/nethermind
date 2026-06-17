// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class TxDecoderTests
    {
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

        public static IEnumerable<(TransactionBuilder<Transaction>, string)> TestObjectsSource()
        {
            yield return (Build.A.Transaction.SignedAndResolved(), "basic");
            yield return (Build.A.Transaction.SignedAndResolved().WithNonce(0), "basic with nonce=0");
            yield return (Build.A.Transaction
                .WithData(new byte[] { 1, 2, 3 })
                .WithType(TxType.AccessList)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithAccessList(
                    new AccessList.Builder()
                        .AddAddress(Address.Zero)
                        .AddStorage(1)
                        .Build())
                .SignedAndResolved(), "access list");
            yield return (Build.A.Transaction
                .WithData(new byte[] { 1, 2, 3 })
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(30)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithAccessList(
                    new AccessList.Builder()
                        .AddAddress(Address.Zero)
                        .AddStorage(1)
                        .Build())
                .SignedAndResolved(), "EIP1559 - access list");
            yield return (Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithMaxFeePerGas(50)
                .WithMaxPriorityFeePerGas(10)
                .WithChainId(0)
                .SignedAndResolved(), "EIP 1559");
            yield return (Build.A.Transaction
                .WithMaxFeePerGas(2.GWei)
                .WithType(TxType.EIP1559)
                .WithGasPrice(0)
                .WithChainId(1559)
                .SignedAndResolved(), "EIP 1559 second test case");
        }

        public static IEnumerable<(Transaction, string)> TestCaseSource()
            => TestObjectsSource().Select(static tos => (tos.Item1.TestObject, tos.Item2));

        [TestCaseSource(nameof(TestCaseSource))]
        [Repeat(10)] // Might wanna increase this to double check when changing logic as on lower value, it does not reproduce.
        public async Task CanCorrectlyCalculateTxHash_when_called_concurrently((Transaction Tx, string Description) testCase)
        {
            Transaction tx = testCase.Tx;

            Rlp rlp = _txDecoder.Encode(tx);

            Hash256 expectedHash = Keccak.Compute(rlp.Bytes);

            RlpReader decoderCtx = new(rlp.Bytes);
            Transaction decodedTx = _txDecoder.Decode(ref decoderCtx)!;

            decodedTx.SetPreHash(rlp.Bytes);

            using ArrayPoolList<Task> tasks = Enumerable
                .Range(0, 32)
                .Select(_ => Task.Factory.StartNew(() => Assert.That(decodedTx.Hash, Is.EqualTo(expectedHash)), TaskCreationOptions.RunContinuationsAsynchronously))
                .ToPooledList(32);

            await Task.WhenAll(tasks.AsSpan());
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip((Transaction Tx, string Description) testCase)
        {
            byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _txDecoder.Encode(ref writer, testCase.Tx);
            RlpReader ctx = new(bytes);
            Transaction? decoded = _txDecoder.Decode(ref ctx);
            decoded!.SenderAddress =
                new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
            decoded.Hash = decoded.CalculateHash();
            Assert.That(decoded, Is.EqualTo(testCase.Tx).UsingTransactionComparer());
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip_RlpReader((Transaction Tx, string Description) testCase)
        {
            byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _txDecoder.Encode(ref writer, testCase.Tx);

            Span<byte> spanIncomingTxRlp = bytes.AsSpan();
            RlpReader decoderContext = new(spanIncomingTxRlp);
            Transaction? decoded = _txDecoder.Decode(ref decoderContext);
            decoded!.SenderAddress =
                new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
            decoded.Hash = decoded.CalculateHash();
            Assert.That(decoded, Is.EqualTo(testCase.Tx).UsingTransactionComparer());
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Roundtrip_RlpReader_WithMemorySlice((Transaction Tx, string Description) testCase)
        {
            byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _txDecoder.Encode(ref writer, testCase.Tx);

            RlpReader decoderContext = new(bytes, true);
            Transaction? decoded = _txDecoder.Decode(ref decoderContext);
            decoded!.SenderAddress =
                new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
            decoded.Hash = decoded.CalculateHash();
            Assert.That(decoded, Is.EqualTo(testCase.Tx).UsingTransactionComparer());
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void RlpReader_DecodeWithMemorySlice_ShouldUseSameBuffer((Transaction Tx, string Description) testCase)
        {
            if (testCase.Tx.Data.Length == 0) return;

            byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
            RlpWriter writer = new(bytes);
            _txDecoder.Encode(ref writer, testCase.Tx);

            RlpReader decoderContext = new(bytes, true);
            Transaction? decoded = _txDecoder.Decode(ref decoderContext);

            byte[] data1 = decoded!.Data.ToArray();
            data1.AsSpan().Fill(1);
            bytes.AsSpan().Fill(1);

            Assert.That(decoded.Data.ToArray(), Is.EqualTo(data1));
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void Roundtrip_yolo_v3((string IncomingRlpHex, Hash256 Hash) testCase)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            byte[] incomingTxRlpBytes = Bytes.FromHexString(testCase.IncomingRlpHex);
            RlpReader ctx = new(incomingTxRlpBytes);

            Transaction decoded = _txDecoder.Decode(ref ctx)!;
            Assert.That(decoded.CalculateHash(), Is.EqualTo(testCase.Hash));

            byte[] ourRlpOutput = new byte[_txDecoder.GetLength(decoded, RlpBehaviors.None)];
            RlpWriter writer = new(ourRlpOutput);
            _txDecoder.Encode(ref writer, decoded);

            string ourRlpHex = ourRlpOutput.AsSpan(0, incomingTxRlpBytes.Length).ToHexString();
            Assert.That(ourRlpHex, Is.EqualTo(testCase.IncomingRlpHex));
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void CalculateHash_and_tx_hash_after_decoding_return_the_same_value(
            (string IncomingRlpHex, Hash256 Hash) testCase)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            byte[] incomingTxRlpBytes = Bytes.FromHexString(testCase.IncomingRlpHex);
            RlpReader ctx = new(incomingTxRlpBytes);
            Transaction decoded = _txDecoder.Decode(ref ctx)!;
            Rlp encodedForTreeRoot = _txDecoder.Encode(decoded, RlpBehaviors.SkipTypedWrapping);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.CalculateHash(), Is.EqualTo(decoded.Hash!));
                Assert.That(decoded.Hash, Is.EqualTo(Keccak.Compute(encodedForTreeRoot.Bytes)));
            }
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void Hash_calculation_do_not_change_after_roundtrip((string IncomingRlpHex, Hash256 Hash) testCase)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            byte[] incomingTxRlpBytes = Bytes.FromHexString(testCase.IncomingRlpHex);
            RlpReader ctx = new(incomingTxRlpBytes);
            Transaction decoded = _txDecoder.Decode(ref ctx)!;
            Rlp encodedForTreeRoot = _txDecoder.Encode(decoded, RlpBehaviors.SkipTypedWrapping);
            Assert.That(decoded.Hash, Is.EqualTo(Keccak.Compute(encodedForTreeRoot.Bytes)));
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void Hash_calculation_do_not_change_after_roundtrip2((string IncomingRlpHex, Hash256 Hash) testCase)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            byte[] incomingTxRlpBytes = Bytes.FromHexString(testCase.IncomingRlpHex);
            RlpReader ctx = new(incomingTxRlpBytes);
            Transaction decoded = _txDecoder.Decode(ref ctx)!;
            Rlp encodedForTreeRoot = _txDecoder.Encode(decoded, RlpBehaviors.SkipTypedWrapping);
            Assert.That(decoded.Hash, Is.EqualTo(Keccak.Compute(encodedForTreeRoot.Bytes)));
        }

        [TestCaseSource(nameof(YoloV3TestCases))]
        public void RlpReader_return_the_same_transaction_with_wrapping(
            (string IncomingRlpHex, Hash256 Hash) testCase) => RlpReader_return_the_same_transaction(testCase, false);

        [TestCaseSource(nameof(SkipTypedWrappingTestCases))]
        public void RlpReader_return_the_same_transaction_without_additional_wrapping(
            (string IncomingRlpHex, Hash256 Hash) testCase) => RlpReader_return_the_same_transaction(testCase, true);

        private void RlpReader_return_the_same_transaction(
            (string IncomingRlpHex, Hash256 Hash) testCase, bool wrapping)
        {
            TestContext.Out.WriteLine($"Testing {testCase.Hash}");
            Span<byte> spanIncomingTxRlp = Bytes.FromHexString(testCase.IncomingRlpHex).AsSpan();
            RlpReader decoderContext = new(spanIncomingTxRlp);
            Transaction decodedByRlpReader = _txDecoder.Decode(ref decoderContext, wrapping ? RlpBehaviors.SkipTypedWrapping : RlpBehaviors.None)!;
            RlpReader ctx2 = new(spanIncomingTxRlp);
            Transaction decoded = _txDecoder.Decode(ref ctx2, wrapping ? RlpBehaviors.SkipTypedWrapping : RlpBehaviors.None)!;
            Rlp encoded = _txDecoder.Encode(decoded);
            Rlp encodedWithDecodedByRlpReader = _txDecoder.Encode(decodedByRlpReader);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(decoded.Hash, Is.EqualTo(testCase.Hash));
                Assert.That(decoded.Hash, Is.EqualTo(decodedByRlpReader.Hash!));
                Assert.That(encodedWithDecodedByRlpReader.Bytes, Is.EqualTo(encoded.Bytes));
            }
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Rlp_encode_should_return_the_same_as_canonical_rlp_encoding(
            (Transaction Tx, string Description) testCase)
        {
            Rlp encoded = _txDecoder.Encode(testCase.Tx, RlpBehaviors.SkipTypedWrapping);
            Rlp rlpResult = Rlp.Encode(testCase.Tx, false, true, testCase.Tx.ChainId ?? 0);
            Assert.That(encoded.Bytes, Is.EqualTo(rlpResult.Bytes));
        }

        [Test]
        public void Duplicate_storage_keys_result_in_different_hashes()
        {
            Transaction noDuplicates = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithAccessList(
                    new AccessList.Builder()
                        .AddAddress(Address.Zero)
                        .AddStorage(1)
                        .Build())
                .SignedAndResolved()
                .TestObject;

            Transaction duplicates = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithAccessList(
                    new AccessList.Builder()
                        .AddAddress(Address.Zero)
                        .AddStorage(1)
                        .AddStorage(1)
                        .Build())
                .SignedAndResolved()
                .TestObject;

            Assert.That(duplicates.CalculateHash(), Is.Not.EqualTo(noDuplicates.CalculateHash()));
        }


        [TestCaseSource(nameof(InvalidEncodingTestCases))]
        public void Rejects_invalid_tx_encoding(byte[] invalidTxBytes, string error, Type exceptionType)
        {
            void DecodeStream()
            {
                RlpReader ctx = new(invalidTxBytes);
                _txDecoder.Decode(ref ctx, RlpBehaviors.SkipTypedWrapping);
            }

            Assert.That(DecodeStream, Throws.InstanceOf(exceptionType).With.Message.Contains(error).IgnoreCase);

            void DecodeContext()
            {
                RlpReader ctx = new(invalidTxBytes.AsSpan());
                _txDecoder.Decode(ref ctx, RlpBehaviors.SkipTypedWrapping);
            }

            Assert.That(DecodeContext, Throws.InstanceOf(exceptionType).With.Message.Contains(error).IgnoreCase);
        }

        public static IEnumerable<(string, Hash256)> SkipTypedWrappingTestCases()
        {
            yield return
            (
                "01f8a486796f6c6f763380843b9aca008262d4948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f838f7940000000000000000000000000000000000001337e1a0000000000000000000000000000000000000000000000000000000000000000080a0775101f92dcca278a56bfe4d613428624a1ebfc3cd9e0bcc1de80c41455b9021a06c9deac205afe7b124907d4ba54a9f46161498bd3990b90d175aac12c9a40ee9",
                new Hash256("0x212a85be428a85d00fb5335b013bc8d3cf7511ffdd8938de768f4ca8bf1caf50")
            );
            yield return
            (
                "01f8c786796f6c6f763301843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a01feaff3227c4fe4954fe5297898027d71eb9ae2291e2b967f00b2f5ccd0597baa053bfeb53c31024700b8d3b226eb60766178b17f215c3a5b5bd7fa2c45db86fb8",
                new Hash256("0x8204f9c9043f170ab4c061c60e690a79f3bdb88d4af69c69d67248b25fb6a4a7")
            );
            yield return
            (
                "01f8c786796f6c6f763302843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000001a05dd874c3cbf30fa22f2612c4dda995e53dda6f3aad335760bccd0fe3ae65dadda056208b02dac8246ecbf4624c8b49302e4869781e630ebba356e13d532166ba5d",
                new Hash256("0x2c876e955d2b656d858cdad0400920fba877b807c70b36ca18b67db96865f6a0")
            );

            yield return
            (
                "01f8c786796f6c6f763303843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a072515bdc69de9eb8e5067ffcd069ec84745ea629cfc60b854edccdd6a9d1d80fa063bb9c012fdb80aabdb915bea8e3c99b574e88cf37daea4dcc535627b48b56f0",
                new Hash256("0x449556a7ee5a1e708a0afcc5110507c6a45e894a3ad6d7e21c50bbe521626229")
            );
            yield return
            (
                "01f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144",
                new Hash256("0x70c0d568f790ee752c9f86073f7687e286b919add9e1d768dc6c5f1812a364f7")
            );
            yield return
            (
                "f86905843b9aca00825208948a8eafb1cf62bfbeb1741769dae1a9dd47996192018086f2ded8deec8aa04135bba08382dae6a1d5ec4b557f2460e1d63fb6f93773a7a951ce38a28a31ada03d36a791688f311252df622a48a9acfb0500fd3584a8305ee004d895c0257400",
                new Hash256("0x593dd0e1bf113b762674470741817c4d823c73fb7377da4f6073c7885585ae92")
            );
            yield return
            (
                "01f8a587796f6c6f76337880843b9aca008262d4948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f838f7940000000000000000000000000000000000001337e1a0000000000000000000000000000000000000000000000000000000000000000001a0e0bfceab9cadc44aa4a2c7f985f773586e2976d19cc54620e95f7d5e24bfeb6aa03225ae7b7ef42716ee5cae1b0f7f05d9777b1d4882d387e4da88cae65095bef8",
                new Hash256("0x5cb00d928abf074cb81fc5e54dd49ef541afa7fc014b8a53fb8c29f3ecb5cadb")
            );
            yield return
            (
                "01f8c887796f6c6f76337801843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000001a0cb68184d42bd56b82abc9fc7cde0190308205264d6af4224d40fb504eee00d5aa036a483e4fd876a9a7817dee78f12decc534d124a55ae2c89c65e6c45452eb2ce",
                new Hash256("0xd570bbb09f5bd9abf8fdc6ec7e036612bcb6b02b25f51ef0e1544f2a539ca3ac")
            );
            yield return
            (
                "01f8c887796f6c6f76337803843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a094f3a6bbf5039b1a794e5be7628809bdf757c4ff59e5399dec74c61137074f80a049baf92bb5fb2d6c6bf8287fcd75eaea80ad38d1b8d29ce242c4ac51e1067d52",
                new Hash256("0x0a956694228afe4577bd94fcf8a3aa8544bbadcecfe0d66ccad8ec7ae56c025f")
            );
            yield return
            (
                "01f85b821e8e8204d7847735940083030d408080853a60005500c080a0f43e70c79190701347517e283ef63753f6143a5225cbb500b14d98eadfb7616ba070893923d8a1fc97499f426524f9e82f8e0322dfac7c3d7e8a9eee515f0bcdc4",
                new Hash256("0x64450bbd000900379235ca8cad7c6f04288b9a9044967e1e1d63c0bc352624e0")
            );
        }

        public static IEnumerable<(string, Hash256)> YoloV3TestCases()
        {
            yield return
            (
                "b8a701f8a486796f6c6f763380843b9aca008262d4948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f838f7940000000000000000000000000000000000001337e1a0000000000000000000000000000000000000000000000000000000000000000080a0775101f92dcca278a56bfe4d613428624a1ebfc3cd9e0bcc1de80c41455b9021a06c9deac205afe7b124907d4ba54a9f46161498bd3990b90d175aac12c9a40ee9",
                new Hash256("0x212a85be428a85d00fb5335b013bc8d3cf7511ffdd8938de768f4ca8bf1caf50")
            );
            yield return
            (
                "b8ca01f8c786796f6c6f763301843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a01feaff3227c4fe4954fe5297898027d71eb9ae2291e2b967f00b2f5ccd0597baa053bfeb53c31024700b8d3b226eb60766178b17f215c3a5b5bd7fa2c45db86fb8",
                new Hash256("0x8204f9c9043f170ab4c061c60e690a79f3bdb88d4af69c69d67248b25fb6a4a7")
            );
            yield return
            (
                "b8ca01f8c786796f6c6f763302843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000001a05dd874c3cbf30fa22f2612c4dda995e53dda6f3aad335760bccd0fe3ae65dadda056208b02dac8246ecbf4624c8b49302e4869781e630ebba356e13d532166ba5d",
                new Hash256("0x2c876e955d2b656d858cdad0400920fba877b807c70b36ca18b67db96865f6a0")
            );

            yield return
            (
                "b8ca01f8c786796f6c6f763303843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a072515bdc69de9eb8e5067ffcd069ec84745ea629cfc60b854edccdd6a9d1d80fa063bb9c012fdb80aabdb915bea8e3c99b574e88cf37daea4dcc535627b48b56f0",
                new Hash256("0x449556a7ee5a1e708a0afcc5110507c6a45e894a3ad6d7e21c50bbe521626229")
            );
            yield return
            (
                "b9018201f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144",
                new Hash256("0x70c0d568f790ee752c9f86073f7687e286b919add9e1d768dc6c5f1812a364f7")
            );
            yield return
            (
                "f86905843b9aca00825208948a8eafb1cf62bfbeb1741769dae1a9dd47996192018086f2ded8deec8aa04135bba08382dae6a1d5ec4b557f2460e1d63fb6f93773a7a951ce38a28a31ada03d36a791688f311252df622a48a9acfb0500fd3584a8305ee004d895c0257400",
                new Hash256("0x593dd0e1bf113b762674470741817c4d823c73fb7377da4f6073c7885585ae92")
            );
            yield return
            (
                "b8a801f8a587796f6c6f76337880843b9aca008262d4948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f838f7940000000000000000000000000000000000001337e1a0000000000000000000000000000000000000000000000000000000000000000001a0e0bfceab9cadc44aa4a2c7f985f773586e2976d19cc54620e95f7d5e24bfeb6aa03225ae7b7ef42716ee5cae1b0f7f05d9777b1d4882d387e4da88cae65095bef8",
                new Hash256("0x5cb00d928abf074cb81fc5e54dd49ef541afa7fc014b8a53fb8c29f3ecb5cadb")
            );
            yield return
            (
                "b8cb01f8c887796f6c6f76337801843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000001a0cb68184d42bd56b82abc9fc7cde0190308205264d6af4224d40fb504eee00d5aa036a483e4fd876a9a7817dee78f12decc534d124a55ae2c89c65e6c45452eb2ce",
                new Hash256("0xd570bbb09f5bd9abf8fdc6ec7e036612bcb6b02b25f51ef0e1544f2a539ca3ac")
            );
            yield return
            (
                "b8cb01f8c887796f6c6f76337803843b9aca00826a40948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f85bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a094f3a6bbf5039b1a794e5be7628809bdf757c4ff59e5399dec74c61137074f80a049baf92bb5fb2d6c6bf8287fcd75eaea80ad38d1b8d29ce242c4ac51e1067d52",
                new Hash256("0x0a956694228afe4577bd94fcf8a3aa8544bbadcecfe0d66ccad8ec7ae56c025f")
            );
        }

        private static IEnumerable<TestCaseData> InvalidEncodingTestCases()
        {
            static TestCaseData TestCase(string testName, byte[] invalidTxBytes, string? error = null, Type? exceptionType = null) =>
                new(invalidTxBytes, error ?? "", exceptionType ?? typeof(RlpException)) { TestName = testName };

            yield return TestCase("Missing storage keys array in access list",
                Convert.FromHexString("01e3010101825208808080d6d5940000000000000000000000000000000000000001010101"),
                error: "storage keys"
            );

            yield return TestCase(
                "Signed legacy tx prefixed with 0-byte (simulating 'legacy' type)",
                [0, .. _txDecoder.Encode(Build.A.Transaction.SignedAndResolved().TestObject).Bytes],
                error: "legacy"
            );

            yield return TestCase(
                "SetCode auth list count over limit",
                BuildSetCodeTxBytes(100_000),
                exceptionType: typeof(RlpLimitException)
            );

            yield return TestCase(
                "SetCode null auth element",
                BuildSetCodeTxBytes(1)
            );
        }

        private static byte[] BuildSetCodeTxBytes(int authCount) => _txDecoder.Encode(new Transaction
        {
            Type = TxType.SetCode,
            ChainId = 1,
            GasLimit = 21000,
            AuthorizationList = new AuthorizationTuple[authCount]
        }, RlpBehaviors.SkipTypedWrapping).Bytes;
    }
}
