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
using DotNetty.Common;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Rlpx.Handshake;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class InFlow
    {
        private static byte[] _input = Bytes.FromHexString("96cf8b950a261eae89f0e0cd0432c7d16aa615fd0633fb0375a5db932fd65a237a4acc5efc408b693073fe8bfb82068dfcf279d80dafce41dbc4f658d92add3bb276063415c4dbacf81bbd2b0a1254eb858522b77417c9e3d6d36d67454c6c45188c642657ffdd5a67c0e2dabd5db24cd8702662f6d041ff896dcf1ef958fa37ef49187302c9ec43ea5cf3828119e84658d397b4646316636dbe4295c5e5b2df69e72c75b32fc03a1e0ec227d3b94fcd4e1f5b593e3dca74d0d327cc2a31402e57f2e62d3b721a8131d40a35e7c2d1babfe3578814f51444b518917e940721eebeabac4b70ad82c21e5270c7434907a92543914698a0cc6c692a33ad6fafc591be2de18e6c297d07a5992cc68adb27ce4a8159a365b551f2cb33e5e5370d3dc2");

        private IByteBuffer _decoderBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private NewBlockMessage _outputMessage;

        private NewBlockMessageSerializer _newBlockMessageSerializer;
        private ZeroNewBlockMessageSerializer _zeroNewBlockMessageSerializer;
        private Block _block;
        private TestMerger _merger;
        private TestZeroMerger _zeroMerger;
        private TestZeroDecoder _zeroDecoder;
        private TestDecoder _decoder;
        private TestSnappy _snappyEncoder;
//        private TestZeroSnappy _zeroSnappyEncoder;
        private NewBlockMessage _newBlockMessage;
        private MessageSerializationService _serializationService;

        [GlobalSetup]
        public void Setup()
        {
            SetupAll();
            Current();
            Check();
//            SetupAll();
//            Improved();
//            Check();
            SetupAll(true);
        }

        private void SetupAll(bool useLimboOutput = false)
        {
            var secrets = NetTestVectors.GetSecretsPair();

            FrameCipher frameCipher = new FrameCipher(secrets.B.AesSecret);
            FrameMacProcessor frameMacProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.B);
            _decoder = new TestDecoder(frameCipher, frameMacProcessor, LimboTraceLogger.Instance);
            _merger = new TestMerger();
            _zeroMerger = new TestZeroMerger();
            _zeroDecoder = new TestZeroDecoder(frameCipher, frameMacProcessor);
            _snappyEncoder = new TestSnappy();
//            _zeroSnappyEncoder = new TestZeroSnappy();
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            _block = Build.A.Block.WithTransactions(a, b).TestObject;
            _newBlockMessageSerializer = new NewBlockMessageSerializer();
            _zeroNewBlockMessageSerializer = new ZeroNewBlockMessageSerializer();

            _newBlockMessage = new NewBlockMessage();
            _newBlockMessage.Block = _block;
            _serializationService = new MessageSerializationService();
            _serializationService.Register(_newBlockMessageSerializer);
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
        }

        private class TestDecoder : NettyFrameDecoder
        {
            public byte[] Decode(IByteBuffer buffer)
            {
                var result = new List<object>();
                base.Decode(null, buffer, result);
                return (byte[]) result[0];
            }

            public TestDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogger logger)
                : base(frameCipher, frameMacProcessor, logger)
            {
            }
        }

        private class TestZeroDecoder : ZeroNettyFrameDecoder
        {
            public IByteBuffer Decode(IByteBuffer input)
            {
                var result = new List<object>();
                base.Decode(null, input, result);
                return (IByteBuffer) result[0];
            }

            public TestZeroDecoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor)
                : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
            {
            }
        }

        private class TestMerger : Rlpx.NettyFrameMerger
        {
            public TestMerger()
                : base(LimboNoErrorLogger.Instance)
            {
            }

            public Packet Decode(byte[] input)
            {
                var result = new List<object>();
                base.Decode(null, input, result);
                return (Packet) result[0];
            }
        }

        private class TestZeroMerger : Rlpx.ZeroNettyFrameMerger
        {
            public TestZeroMerger()
                : base(LimboLogs.Instance)
            {
            }

            public IByteBuffer Decode(IByteBuffer input)
            {
                var result = new List<object>();
                base.Decode(null, input, result);
                return (IByteBuffer) result[0];
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

//        public class TestZeroSnappy : ZeroSnappyDecoder
//        {
//            public TestZeroSnappy()
//                : base(LimboLogs.Instance)
//            {
//            }
//
//            public void TestEncode(IByteBuffer input, IByteBuffer output)
//            {
//                Encode(null, input, output);
//            }
//        }

        private void Check()
        {
            if (_outputMessage.Block.Transactions.Length != 2)
            {
                throw new Exception();
            }
        }

//        [Benchmark]
//        public void Improved()
//        {
//            _decoderBuffer.WriteBytes(_input);
//            IByteBuffer decoded = _zeroDecoder.Decode(_decoderBuffer);
//            IByteBuffer merged = _zeroMerger.Decode(decoded);
//            _outputMessage = _newBlockMessageSerializer.Deserialize(merged.ReadAllBytes());
//        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _decoderBuffer.WriteBytes(_input);
            byte[] decoded = _decoder.Decode(_decoderBuffer);
            
            Packet merged = _merger.Decode(decoded);
            throw new Exception(decoded.ToHexString());
            _outputMessage = _newBlockMessageSerializer.Deserialize(merged.Data);
        }
    }
}