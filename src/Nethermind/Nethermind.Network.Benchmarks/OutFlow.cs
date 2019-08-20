/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class OutFlow
    {
        private static byte[] _expectedResult = Bytes.FromHexString("e13025bd4ae2d72b35e6f05a3b2f3aacf9ffe78eb851f84dc3264380eac186032b9d5d7350d1271323fe1a6c5aeea2b9e9d6d25e317ab957d737577b84de62fe4107cafcc795f832b71b71344fa44317ba4e113df762f4fa5dd7150e1a288d62f5d72438d56e3eda3aed9a4ba1be7eadceb782cf8e48a7ff6a521282388c8a88ac293ce26fad579cd1ea2ae80705856da9b9b33b5ef46b64ee3d44d2ecaa8e0d2d932fdf29d1d575e3266bb6524acfc438687a45c492815481698e0e1860c7f854b3918eb6550bd867dbc417c808ef9c746ac6d605b39a26c731476d3c9d5bea8c095b6e212a8f1575f9287ac04191c912891fcea59f91d555c59621cc80f1ef41bf7c941b4816eae18821a15ca39fc81726079e7056490dffa3d190cae9d698");
        private byte[] _actualResult = new byte[_expectedResult.Length];
        private IByteBuffer _snappyBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _splitterBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _encoderBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _outputBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);

        private NewBlockMessageSerializer _newBlockMessageSerializer;
        private ZeroNewBlockMessageSerializer _zeroNewBlockMessageSerializer;
        private Block _block;
        private TestSplitter _splitter;
        private TestZeroSplitter _zeroSplitter;
        private TestZeroEncoder _zeroEncoder;
        private TestEncoder _encoder;
        private TestSnappy _snappyEncoder;
        private TestZeroSnappy _zeroSnappyEncoder;
        private NewBlockMessage _newBlockMessage;

        [GlobalSetup]
        public void Setup()
        {
//            SetupAll();
//            Current();
//            Check();
//            SetupAll();
//            Improved();
//            Check();
            SetupAll(true);
        }

        private void SetupAll(bool useLimboOutput = false)
        {
            PublicKey publicKey = new PublicKey(
                "000102030405060708090A0B0C0D0E0F" +
                "101112131415161718191A1B1C1D1E1F" +
                "202122232425262728292A2B2C2D2E2F" +
                "303132333435363738393A3B3C3D3E3F");
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = Keccak.EmptyTreeHash.Bytes;
            secrets.MacSecret = Keccak.OfAnEmptySequenceRlp.Bytes;
            secrets.Token = Keccak.OfAnEmptyString.Bytes;
            secrets.EgressMac = new KeccakDigest(256);
            secrets.IngressMac = new KeccakDigest(256);

            FrameCipher frameCipher = new FrameCipher(secrets.AesSecret);
            FrameMacProcessor frameMacProcessor = new FrameMacProcessor(publicKey, secrets);
            _encoder = new TestEncoder(frameCipher, frameMacProcessor, LimboTraceLogger.Instance);
            _splitter = new TestSplitter();
            _splitter.DisableFraming();
            _zeroSplitter = new TestZeroSplitter();
            _zeroSplitter.DisableFraming();
            _zeroEncoder = new TestZeroEncoder(frameCipher, frameMacProcessor, LimboTraceLogger.Instance);
            _snappyEncoder = new TestSnappy();
            _zeroSnappyEncoder = new TestZeroSnappy();
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            _block = Build.A.Block.WithTransactions(a, b).TestObject;
            _newBlockMessageSerializer = new NewBlockMessageSerializer();
            _zeroNewBlockMessageSerializer = new ZeroNewBlockMessageSerializer();
            if (useLimboOutput)
            {
                _outputBuffer = new MockBuffer();
            }
            
            _newBlockMessage = new NewBlockMessage();
            _newBlockMessage.Block = _block;
        }

        private class TestEncoder : Rlpx.NettyFrameEncoder
        {
            public void Encode(byte[] message, IByteBuffer buffer)
            {
                base.Encode(null, message, buffer);
            }

            public TestEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogger logger)
                : base(frameCipher, frameMacProcessor, logger)
            {
            }
        }

        private class TestZeroEncoder : Rlpx.ZeroNettyFrameEncoder
        {
            public void Encode(IByteBuffer message, IByteBuffer buffer)
            {
                base.Encode(null, message, buffer);
            }

            public TestZeroEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogger logger)
                : base(frameCipher, frameMacProcessor, logger)
            {
            }
        }

        private class TestSplitter : Rlpx.NettyPacketSplitter
        {
            public void Encode(Packet message, List<object> output)
            {
                base.Encode(null, message, output);
            }
        }

        private class TestZeroSplitter : Rlpx.ZeroNettyPacketSplitter
        {
            public void Encode(IByteBuffer input, IByteBuffer output)
            {
                base.Encode(null, input, output);
            }
        }

        public class TestSnappy : SnappyEncoder
        {
            public TestSnappy()
                : base(NullLogger.Instance)
            {
            }

            public Packet TestEncode(Packet input)
            {
                List<object> result = new List<object>();
                Encode(null, input, result);
                return (Packet) result[0];
            }
        }
        
        public class TestZeroSnappy : ZeroSnappyEncoder
        {
            public TestZeroSnappy()
                : base(NullLogger.Instance)
            {
            }

            public void TestEncode(IByteBuffer input, IByteBuffer output)
            {
                Encode(null, input, output);
            }
        }

        private void Check()
        {
            if (_outputBuffer.ReadableBytes != _expectedResult.Length)
            {
                throw new Exception($"Length wrong - expected:{_expectedResult.Length} - was:{_outputBuffer.ReadableBytes}");
            }

            _outputBuffer.ReadBytes(_actualResult, 0, _outputBuffer.ReadableBytes);
            if (!Bytes.AreEqual(_actualResult, _expectedResult))
            {
                throw new Exception($"Different - expected:{_expectedResult.ToHexString()} - was:{_actualResult.ToHexString()}");
            }
        }
        
        [Benchmark]
        public void Improved()
        {
            _zeroNewBlockMessageSerializer.Serialize(_snappyBuffer, _newBlockMessage);
            _zeroSnappyEncoder.TestEncode(_snappyBuffer, _splitterBuffer);
            _zeroSplitter.Encode(_splitterBuffer, _encoderBuffer);
            _zeroEncoder.Encode(_encoderBuffer, _outputBuffer);
        }
//
//        [Benchmark(Baseline = true)]
//        public void Current()
//        {
//            byte[] message = _newBlockMessageSerializer.Serialize(_newBlockMessage);
//
//            Packet packet = new Packet("eth", 7, message);
//            Packet ensnapped = _snappyEncoder.TestEncode(packet);
//            List<object> output = new List<object>();
//            _splitter.Encode(ensnapped, output);
//            _encoder.Encode((byte[]) output[0], _outputBuffer);
//        }
    }
}