// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.Test;

namespace Nethermind.Network.Benchmarks
{
    public class HandshakeBenchmarks
    {
        [GlobalSetup]
        public void SetUp()
        {
            _trueCryptoRandom = new CryptoRandom();

            _testRandom = new BenchmarkTestRandom(_trueCryptoRandom, 6);

            _messageSerializationService = new MessageSerializationService();
            _messageSerializationService.Register(new AuthMessageSerializer());
            _messageSerializationService.Register(new AuthEip8MessageSerializer(new Eip8MessagePad(_testRandom)));
            _messageSerializationService.Register(new AckMessageSerializer());
            _messageSerializationService.Register(new AckEip8MessageSerializer(new Eip8MessagePad(_testRandom)));

            _eciesCipher = new EciesCipher(_trueCryptoRandom); // TODO: provide a separate test random with specific IV and ephemeral key for testing

            _initiatorService = new HandshakeService(_messageSerializationService, _eciesCipher, _testRandom, _ecdsa, NetTestVectors.StaticKeyA, LimboLogs.Instance);
            _recipientService = new HandshakeService(_messageSerializationService, _eciesCipher, _testRandom, _ecdsa, NetTestVectors.StaticKeyB, LimboLogs.Instance);
        }

        private class BenchmarkTestRandom : ICryptoRandom
        {
            private readonly int _mod;
            private byte[][] responses;
            private int _i;

            public BenchmarkTestRandom(ICryptoRandom trueRandom, int mod)
            {
                responses = new byte[6][];
                _mod = mod;
                _i = -1;

                // WARN: order reflects the internal implementation of the service (tests may fail after any refactoring)
                responses[0] = NetTestVectors.NonceA;
                responses[1] = NetTestVectors.EphemeralKeyA.KeyBytes;
                responses[2] = trueRandom.GenerateRandomBytes(100);
                responses[3] = NetTestVectors.NonceB;
                responses[4] = NetTestVectors.EphemeralKeyB.KeyBytes;
                responses[5] = trueRandom.GenerateRandomBytes(100);
            }

            public byte[] GenerateRandomBytes(int length)
            {
                _i = (_i + 1) % _mod;
                return (byte[])responses[_i].Clone();
            }

            public void GenerateRandomBytes(Span<byte> bytes)
            {
                GenerateRandomBytes(bytes.Length).CopyTo(bytes);
            }

            public int NextInt(int max)
            {
                return max / 2;
            }

            public void Dispose()
            {

            }
        }

        private readonly IEthereumEcdsa _ecdsa = new EthereumEcdsa(BlockchainIds.Sepolia, LimboLogs.Instance); // TODO: separate general crypto signer from Ethereum transaction signing

        private IMessageSerializationService _messageSerializationService;

        private ICryptoRandom _testRandom;

        private ICryptoRandom _trueCryptoRandom;

        private IEciesCipher _eciesCipher;

        private IHandshakeService _initiatorService;

        private IHandshakeService _recipientService;

        private EncryptionHandshake _initiatorHandshake;

        private Packet _auth;

        private Packet _ack;

        private void Auth()
        {
            _initiatorHandshake = new EncryptionHandshake(); // 80B
            _auth = _initiatorService.Auth(NetTestVectors.StaticKeyB.PublicKey, _initiatorHandshake); // 64B
        }

        private void Ack()
        {
            _ack = _recipientService.Ack(new EncryptionHandshake(), _auth);
        }

        private void Agree()
        {
            _initiatorService.Agree(_initiatorHandshake, _ack);
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            Auth();
            Ack();
            Agree();
        }

        [Benchmark]
        public void CurrentAuth()
        {
            Auth();
        }

        [Benchmark]
        public void CurrentAuthAck()
        {
            Auth();
            Ack();
        }
    }
}
