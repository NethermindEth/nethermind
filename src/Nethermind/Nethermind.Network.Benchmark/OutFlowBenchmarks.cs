// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;

namespace Nethermind.Network.Benchmarks
{
    public class OutFlowBenchmarks
    {
        private static byte[] _expectedResult = Bytes.FromHexString("96cf8b950a261eae89f0e0cd0432c7d16aa615fd0633fb0375a5db932fd65a23fc4acc5efc408b693073fe8bfb82068dfcf279d80dafce41dbc4f658d92add3bb276063415c4dbacf81bbd2b0a1254eb858522b77417c9e3d6d36d67454c6c45188c642657ffdd5a67c0e2dabd5db24cd8702662f6d041ff896dcf1ef958fa37ef49187302c9ec43ea5cf3828119e84658d397b4646316636dbe4295c5e5b2df69e72c75b32fc03a1e0ec227d3b94fcd4e1f5b593e3dca74d0d327cc2a31402e57f2e62d3b721a8131d40a35e7c2d1babfe3578814f51444b518917e940721eebeabac4b70ad82c21e5270c7434907a92543914698a0cc6c692a33ad6fafc591be2de18e6c297d07a5992cc68adb27cec4705dc9ac0acb01b65674577932766c");
        private byte[] _actualResult = new byte[_expectedResult.Length];
        private IByteBuffer _snappyBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _splitterBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _encoderBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private IByteBuffer _outputBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);

        private NewBlockMessageSerializer _newBlockMessageSerializer;
        private Block _block;
        private TestZeroSplitter _zeroSplitter;
        private TestZeroEncoder _zeroEncoder;
        private TestZeroSnappy _zeroSnappyEncoder;
        private NewBlockMessage _newBlockMessage;
        private MessageSerializationService _serializationService;

        [GlobalSetup]
        public void Setup()
        {
            SetupAll();
            Current();
            Check();
            SetupAll(true);
        }

        private void SetupAll(bool useLimboOutput = false)
        {
            var secrets = NetTestVectors.GetSecretsPair();

            FrameCipher frameCipher = new FrameCipher(secrets.A.AesSecret);
            FrameMacProcessor frameMacProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.A);
            _zeroSplitter = new TestZeroSplitter();
            _zeroSplitter.DisableFraming();
            _zeroEncoder = new TestZeroEncoder(frameCipher, frameMacProcessor);
            _zeroSnappyEncoder = new TestZeroSnappy();
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            _block = Build.A.Block.WithTransactions(a, b).TestObject;
            _newBlockMessageSerializer = new NewBlockMessageSerializer();
            if (useLimboOutput)
            {
                _outputBuffer = new MockBuffer();
            }

            _newBlockMessage = new NewBlockMessage();
            _newBlockMessage.Block = _block;
            _serializationService = new MessageSerializationService(
                SerializerInfo.Create(_newBlockMessageSerializer)
                );
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
        }

        private class TestZeroEncoder : ZeroFrameEncoder
        {
            public void Encode(IByteBuffer message, IByteBuffer buffer)
            {
                base.Encode(null, message, buffer);
            }

            public TestZeroEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor)
                : base(frameCipher, frameMacProcessor, LimboLogs.Instance)
            {
            }
        }

        private class TestZeroSplitter : ZeroPacketSplitter
        {
            public TestZeroSplitter()
                : base(LimboLogs.Instance)
            {
            }

            public void Encode(IByteBuffer input, IByteBuffer output)
            {
                base.Encode(null, input, output);
            }
        }

        public class TestZeroSnappy : ZeroSnappyEncoder
        {
            public TestZeroSnappy()
                : base(LimboLogs.Instance)
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

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _newBlockMessageSerializer.Serialize(_snappyBuffer, _newBlockMessage);
            _zeroSnappyEncoder.TestEncode(_snappyBuffer, _splitterBuffer);
            _zeroSplitter.Encode(_splitterBuffer, _encoderBuffer);
            _zeroEncoder.Encode(_encoderBuffer, _outputBuffer);
        }
    }
}
