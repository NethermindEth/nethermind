// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Google.Protobuf;
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

using G1 = Bls.P1;
using G2 = Bls.P2;

class ShutterTxSourceTests
{
    // private ShutterTxSource? _shutterTxSource;

    // [SetUp]
    // public void SetUp()
    // {
    //     ILogFinder logFinder = Substitute.For<ILogFinder>();
    //     Shutter.Contracts.IValidatorRegistryContract validatorRegistryContract = Substitute.For<Shutter.Contracts.IValidatorRegistryContract>();

    //     List<IFilterLog> logs = new();

    //     for (byte i = 0; i < 5; i++)
    //     {
    //         IFilterLog log = Substitute.For<IFilterLog>();

    //         byte[] encryptedData = Enumerable.Repeat(i, 5).ToArray();
    //         byte[] identityPrefix = Enumerable.Repeat((byte)0, 32).ToArray();
    //         object[] data = [0L, identityPrefix, Address.Zero, encryptedData, new UInt256(100)];
    //         byte[] encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, ShutterTxSource.TransactionSubmmitedSig, data);

    //         log.Data.Returns(encodedData); ;
    //         logs.Add(log);
    //     }

    //     logFinder.FindLogs(Arg.Any<LogFilter>()).Returns(logs);
    //     _shutterTxSource = new(logFinder, new FilterStore(), validatorRegistryContract, []);
    // }

    // [Test]
    // public void Can_get_transactions_from_logs()
    // {
    //     IEnumerable<ShutterTxSource.SequencedTransaction> txs = _shutterTxSource!.GetNextTransactions(0, 0);
    //     txs.Count().Should().Be(3);
    //     txs.ElementAt(0).EncryptedTransaction.Should().Equal([0, 0, 0, 0, 0]);
    //     txs.ElementAt(1).EncryptedTransaction.Should().Equal([1, 1, 1, 1, 1]);
    //     txs.ElementAt(2).EncryptedTransaction.Should().Equal([2, 2, 2, 2, 2]);
    // }

    // [Test]
    // public void Can_decrypt_sequenced_transaction()
    // {
    //     Transaction transaction = Build.A.Transaction
    //         .WithData([10, 5])
    //         .WithType(TxType.EIP1559)
    //         .TestObject;
    //     byte[] encodedTransaction = Rlp.Encode(transaction, RlpBehaviors.AllowUnsigned).Bytes;
    //     Transaction expected = Rlp.Decode<Transaction>(encodedTransaction, RlpBehaviors.AllowUnsigned);

    //     Bytes32 identityPrefix = new(Enumerable.Repeat((byte)99, 32).ToArray());
    //     G1 identity = ShutterCrypto.ComputeIdentity(identityPrefix, Address.Zero);

    //     UInt256 sk = 123456789;
    //     G2 eonKey = G2.generator().mult(sk.ToLittleEndian());
    //     Bytes32 sigma = new([0x12, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x15, 0xaa, 0xbb, 0x33, 0xfd, 0x66, 0x55, 0x22, 0x88, 0x45]);
    //     ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCryptoTests.Encrypt(encodedTransaction, identity, eonKey, sigma);

    //     ShutterTxSource.SequencedTransaction sequencedTransaction = new()
    //     {
    //         Eon = 0,
    //         EncryptedTransaction = EncodeEncryptedMessage(encryptedMessage),
    //         GasLimit = 99999,
    //         Identity = identity
    //     };

    //     G1 key = identity.dup().mult(sk.ToLittleEndian());
    //     Shutter.Dto.Key decryptionKey = new()
    //     {
    //         Key_ = ByteString.CopyFrom(key.compress()),
    //         Identity = ByteString.CopyFrom(identity.compress())
    //     };

    //     Transaction decryptedTransaction = _shutterTxSource!.DecryptSequencedTransaction(sequencedTransaction, decryptionKey);
    //     Assert.That(decryptedTransaction.Hash, Is.EqualTo(expected.Hash));
    // }

    // private byte[] EncodeEncryptedMessage(ShutterCrypto.EncryptedMessage encryptedMessage)
    // {
    //     byte[] encoded = new byte[96 + 32 + (encryptedMessage.c3.Count() * 32)];

    //     encryptedMessage.c1.compress().CopyTo(encoded, 0);
    //     encryptedMessage.c2.Unwrap().CopyTo(encoded, 96);
    //     foreach ((Bytes32 block, int i) in encryptedMessage.c3.WithIndex())
    //     {
    //         block.Unwrap().CopyTo(encoded, 96 + 32 + (i * 32));
    //     }

    //     return encoded;
    // }
}
