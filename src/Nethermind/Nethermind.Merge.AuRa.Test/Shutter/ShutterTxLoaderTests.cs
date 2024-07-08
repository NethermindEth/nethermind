// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using NSubstitute;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Serialization.Rlp;
using System.Linq;
using Nethermind.Specs.Forks;
using Nethermind.Specs;

namespace Nethermind.Merge.AuRa.Test;

using G1 = Bls.P1;
using G2 = Bls.P2;
using EncryptedMessage = ShutterCrypto.EncryptedMessage;
using SequencedTransaction = ShutterTxLoader.SequencedTransaction;

class ShutterTxLoaderSourceTests
{
    private ShutterTxLoader _txLoader;
    private IEthereumEcdsa _ecdsa;

    [SetUp]
    public void Setup()
    {
        _ecdsa = new EthereumEcdsa(BlockchainIds.Chiado, LimboLogs.Instance);
        ShutterConfig cfg = new()
        {
            SequencerContractAddress = "0x0000000000000000000000000000000000000000"
        };

        _txLoader = new ShutterTxLoader(
            Substitute.For<ILogFinder>(),
            cfg,
            ChiadoSpecProvider.Instance,
            _ecdsa,
            Substitute.For<IReadOnlyBlockTree>(),
            LimboLogs.Instance
        );
    }

    [Test]
    public void Can_decrypt_sequenced_transactions()
    {
        const int txCount = 100;
        UInt256 sk = 4328942385;
        Random rnd = new Random(100);

        byte[] msg = Convert.FromHexString("f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115");

        List<SequencedTransaction> sequencedTransactions = [];
        List<(byte[] IdentityPreimage, byte[] Key)> keys = [];

        for (int i = 0; i < txCount; i++)
        {
            byte[] identityPrefix = new byte[52];
            byte[] sigma = new byte[32];
            rnd.NextBytes(identityPrefix);
            rnd.NextBytes(sigma);

            (SequencedTransaction sequencedTransaction, (byte[], byte[]) key) = GenerateSequencedTransaction(i, msg, identityPrefix, sk, new(sigma));
            sequencedTransactions.Add(sequencedTransaction);
            keys.Add(key);
        }

        // decryption keys are sorted by preimage
        keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));
        keys.Insert(0, new());

        Transaction[] txs = _txLoader.DecryptSequencedTransactions(sequencedTransactions, keys);
        foreach (Transaction tx in txs)
        {
            byte[] tmp = Rlp.Encode<Transaction>(tx).Bytes;
            Assert.That(Enumerable.SequenceEqual(tmp, msg));
        }
        Assert.That(txs.Length, Is.EqualTo(txCount));
    }

    [Test]
    [TestCase(2,
        new string[] {
            // valid
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a0df1cd95e75d0188cded14137c9c83a3ce6d710886bf9139a10cab20dd693ab85a020f5fdae2704bf133be02897c886ceb9189a9ea363989b11330461a78b9bb368",
            "02f8758227d81385012a05f20085012a05f2088252089497d2eeb65da0c37dc0f43ff4691e521673efadfd872386f26fc1000080c080a0c00874a71afda5444b961f78774196fbb833c33482d6463b97380147dd7d472fa061d508b02cb212c78d0b864a04b10e0b0e3accca6e08252049d999c4629cd9a8",
            // wrong chain id
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd5a0b806b9e17c30c4eaad51b290714a407925c82818311a432e4ee656ad23938852a045cd3f087a1f2580ba7d806fa6ba2bfc9933b317ee89fa67713665aab7c22441",
            // bad signature
            "f869820a56849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd4a09999999999999999999999999999999999999999999999999999999999999999a09999999999999999999999999999999999999999999999999999999999999999"
        }
    )]
    public void Can_filter_invalid_transactions(int expectedValid, string[] transactionHexes)
    {
        Transaction[] transactions = new Transaction[transactionHexes.Length];
        for (int i = 0; i < transactionHexes.Length; i++)
        {
            Transaction tx = Rlp.Decode<Transaction>(Convert.FromHexString(transactionHexes[i]));
            tx.SenderAddress = _ecdsa.RecoverAddress(tx, true);
            transactions[i] = tx;
        }

        IReleaseSpec releaseSpec = Cancun.Instance;
        IEnumerable<Transaction> filtered = _txLoader.FilterTransactions(transactions, releaseSpec);
        Assert.That(filtered.Count, Is.EqualTo(expectedValid));
    }

    private (SequencedTransaction, (byte[] IdentityPreimage, byte[] Key)) GenerateSequencedTransaction(int index, byte[] msg, byte[] identityPreimage, UInt256 sk, Bytes32 sigma)
    {
        G1 identity = ShutterCrypto.ComputeIdentity(identityPreimage);
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());

        EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        var sequencedTransaction = new SequencedTransaction()
        {
            Index = index,
            Eon = 0,
            EncryptedTransaction = ShutterCrypto.EncodeEncryptedMessage(encryptedMessage),
            GasLimit = 0,
            Identity = identity,
            IdentityPreimage = identityPreimage
        };

        return (sequencedTransaction, (identityPreimage, key.compress()));
    }
}
