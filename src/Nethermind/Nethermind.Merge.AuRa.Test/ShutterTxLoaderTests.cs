// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using NUnit.Framework;
using Nethermind.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Extensions;
using NSubstitute;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Serialization.Rlp;
using System.Linq;

namespace Nethermind.Merge.AuRa.Test;

using G1 = Bls.P1;
using G2 = Bls.P2;
using EncryptedMessage = ShutterCrypto.EncryptedMessage;
using SequencedTransaction = ShutterTxLoader.SequencedTransaction;

class ShutterTxLoaderSourceTests
{
    [Test]
    public void Can_decrypt_sequenced_transactions()
    {
        ILogManager logManager = new NUnitLogManager();
        ulong chainId = BlockchainIds.Chiado;
        UInt256 sk = 4328942385;
        Random rnd = new Random(100);

        byte[] msg = Convert.FromHexString("f869820248849502f900825208943834a349678ef446bae07e2aeffc01054184af008203e880824fd3a001e44318458b1f279bf81aef969df1b9991944bf8b9d16fd1799ed5b0a7986faa058f572cce63aaff3326df9c902d338b0c416c8fb93109446d6aadd5a65d3d115");

        List<SequencedTransaction> sequencedTransactions = [];
        List<(byte[] IdentityPreimage, byte[] Key)> keys = [];

        for (int i = 0; i < 100; i++)
        {
            byte[] identityPrefix = new byte[52];
            byte[] sigma = new byte[32];
            rnd.NextBytes(identityPrefix);
            rnd.NextBytes(sigma);

            (SequencedTransaction sequencedTransaction, (byte[], byte[]) key) = GenerateSequencedTransaction(msg, identityPrefix, sk, new(sigma));
            sequencedTransactions.Add(sequencedTransaction);
            keys.Add(key);
        }

        // decryption keys are sorted by preimage
        keys.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        ShutterConfig shutterConfig = new()
        {
            SequencerContractAddress = "0x0000000000000000000000000000000000000000"
        };

        ShutterTxLoader txLoader = new ShutterTxLoader(
            Substitute.For<ILogFinder>(),
            shutterConfig,
            Substitute.For<ISpecProvider>(),
            new EthereumEcdsa(chainId, logManager),
            Substitute.For<IReadOnlyBlockTree>(),
            logManager
        );

        Transaction[] txs = txLoader.DecryptSequencedTransactions(sequencedTransactions, keys);
        foreach (Transaction tx in txs)
        {
            byte[] tmp = Rlp.Encode<Transaction>(tx).Bytes;
            Assert.That(Enumerable.SequenceEqual(tmp, msg));
        }
    }

    [Test]
    public void Can_filter_invalid_transactions()
    {
        // TestBlockchain
    }

    private (SequencedTransaction, (byte[] IdentityPreimage, byte[] Key)) GenerateSequencedTransaction(byte[] msg, byte[] identityPreimage, UInt256 sk, Bytes32 sigma)
    {
        G1 identity = ShutterCrypto.ComputeIdentity(identityPreimage);
        G2 eonKey = G2.generator().mult(sk.ToLittleEndian());

        EncryptedMessage encryptedMessage = ShutterCrypto.Encrypt(msg, identity, eonKey, sigma);
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        var sequencedTransaction = new SequencedTransaction()
        {
            Index = 0,
            Eon = 0,
            EncryptedTransaction = ShutterCrypto.EncodeEncryptedMessage(encryptedMessage),
            GasLimit = 0,
            Identity = identity,
            IdentityPreimage = identityPreimage
        };

        return (sequencedTransaction, (identityPreimage, key.compress()));
    }
}
