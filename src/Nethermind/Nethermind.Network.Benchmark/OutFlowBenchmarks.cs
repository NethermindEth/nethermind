// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core;
using Nethermind.Core.Buffers;
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
                using PooledBuffer input = PooledBuffer.Rent(_newBlockMessageSerializer.GetLength(_newBlockMessage, out _));
                _newBlockMessageSerializer.Serialize(input.Span, _newBlockMessage);
                // The pipeline takes ownership of the wrapped view and releases it.
                oracle.WriteOutbound(Wrap(input));
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
            _outputBuffer.Clear();
            using PooledBuffer serialized = PooledBuffer.Rent(_newBlockMessageSerializer.GetLength(_newBlockMessage, out _));
            _newBlockMessageSerializer.Serialize(serialized.Span, _newBlockMessage);
            IByteBuffer wrapped = Wrap(serialized);
            try
            {
                _zeroSplitter.Encode(wrapped, _outputBuffer);
            }
            finally
            {
                wrapped.Release();
            }
        }

        private static IByteBuffer Wrap(PooledBuffer buffer)
        {
            if (!MemoryMarshal.TryGetArray(buffer.ReadOnlyMemory, out ArraySegment<byte> segment) ||
                segment.Array is null)
            {
                throw new InvalidOperationException("Pooled message buffer is not array-backed.");
            }

            return Unpooled.WrappedBuffer(segment.Array, segment.Offset, segment.Count);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _frameMacProcessor?.Dispose();
            _outputBuffer.Release();
        }
    }
}
