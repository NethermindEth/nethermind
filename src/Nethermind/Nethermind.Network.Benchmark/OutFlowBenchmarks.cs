// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Transport.Channels.Embedded;
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
        private IByteBuffer _snappyBuffer = PooledByteBufferAllocator.Default.Buffer(MemorySizes.MiB);
        private IByteBuffer _outputBuffer = PooledByteBufferAllocator.Default.Buffer(MemorySizes.MiB);

        private NewBlockMessageSerializer _newBlockMessageSerializer;
        private Block _block;
        private TestZeroSplitter _zeroSplitter;
        private FrameMacProcessor _frameMacProcessor;
        private NewBlockMessage _newBlockMessage;
        private MessageSerializationService _serializationService;

        [GlobalSetup]
        public void Setup()
        {
            SetupAll();
            Current();
            Check();
            SetupAll();
        }

        private void SetupAll()
        {
            (EncryptionSecrets A, EncryptionSecrets B) secrets = NetTestVectors.GetSecretsPair();

            FrameCipher frameCipher = new(secrets.A.AesSecret);
            _frameMacProcessor?.Dispose();
            _frameMacProcessor = new(TestItem.IgnoredPublicKey, secrets.A);
            _zeroSplitter = new TestZeroSplitter(frameCipher, _frameMacProcessor);
            _zeroSplitter.EnableSnappy(LimboLogs.Instance);
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            _block = Build.A.Block.WithTransactions(a, b).TestObject;
            _newBlockMessageSerializer = new NewBlockMessageSerializer();
            _newBlockMessage = new NewBlockMessage();
            _newBlockMessage.Block = _block;
            _serializationService = new MessageSerializationService(
                SerializerInfo.Create(_newBlockMessageSerializer)
                );
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
        }

        private class TestZeroSplitter(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor)
            : ZeroPacketSplitter(frameCipher, frameMacProcessor)
        {
            public void Encode(IByteBuffer input, IByteBuffer output) => base.Encode(null, input, output);
        }

        private void Check()
        {
            (EncryptionSecrets secrets, _) = NetTestVectors.GetSecretsPair();
            using FrameMacProcessor mac = new(TestItem.IgnoredPublicKey, secrets);
            ZeroPacketSplitter splitter = new();
            splitter.DisableFraming();
            EmbeddedChannel oracle = new(
                new ZeroFrameEncoder(new FrameCipher(secrets.AesSecret), mac),
                splitter,
                new ZeroSnappyEncoder(LimboLogs.Instance));
            try
            {
                IByteBuffer input = Unpooled.Buffer();
                _newBlockMessageSerializer.Serialize(input, _newBlockMessage);
                oracle.WriteOutbound(input);
                IByteBuffer expected = oracle.ReadOutbound<IByteBuffer>();
                try
                {
                    byte[] expectedBytes = new byte[expected.ReadableBytes];
                    byte[] actualBytes = new byte[_outputBuffer.ReadableBytes];
                    expected.ReadBytes(expectedBytes);
                    _outputBuffer.ReadBytes(actualBytes);
                    if (!Bytes.AreEqual(actualBytes, expectedBytes))
                    {
                        throw new Exception("Combined encoding differs from the split-pipeline oracle.");
                    }
                }
                finally
                {
                    expected.Release();
                }
            }
            finally
            {
                oracle.FinishAndReleaseAll();
            }
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _snappyBuffer.Clear();
            _outputBuffer.Clear();
            _newBlockMessageSerializer.Serialize(_snappyBuffer, _newBlockMessage);
            _zeroSplitter.Encode(_snappyBuffer, _outputBuffer);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _frameMacProcessor?.Dispose();
            _snappyBuffer.Release();
            _outputBuffer.Release();
        }
    }
}
