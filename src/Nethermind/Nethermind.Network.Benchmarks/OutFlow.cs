using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Org.BouncyCastle.Crypto.Digests;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    [CoreJob(baseline: true)]
    public class OutFlow
    {
        private TestSplitter _splitter;
        private TestEncoder _encoder;
        private TestSnappy _snappyEncoder;
        private PacketSender _packetSender;
        private byte[] message = new byte[512];
        private IByteBuffer _byteBuffer;
        
        [GlobalSetup]
        public void Setup()
        {
            CryptoRandom random = new CryptoRandom();
            EncryptionSecrets secrets = new EncryptionSecrets();
            secrets.AesSecret = Keccak.EmptyTreeHash.Bytes;
            secrets.MacSecret = Keccak.OfAnEmptySequenceRlp.Bytes;
            secrets.Token = Keccak.OfAnEmptyString.Bytes;
            secrets.EgressMac = new KeccakDigest(256);
            secrets.IngressMac = new KeccakDigest(256);
            
            FrameCipher frameCipher = new FrameCipher(secrets.AesSecret);
            FrameMacProcessor frameMacProcessor = new FrameMacProcessor(new PublicKey(random.GenerateRandomBytes(64)), secrets);
            _encoder = new TestEncoder(frameCipher, frameMacProcessor, LimboTraceLogger.Instance);
            _splitter = new TestSplitter();
            _splitter.DisableFraming();
            _snappyEncoder = new TestSnappy();
            _packetSender = new PacketSender(LimboLogs.Instance);
            _byteBuffer = PooledByteBufferAllocator.Default.Buffer(1024);
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
        
        private class TestSplitter : Rlpx.NettyPacketSplitter
        {
            public void Encode(Packet message, List<object> output)
            {
                base.Encode(null, message, output);
            }
        }
        
        public class TestSnappy : SnappyEncoder
        {
            public TestSnappy()
                : base(NullLogger.Instance)
            {
                
            }
            
            public Packet TestEncode(byte[] input)
            {
                List<object> result = new List<object>();
                Encode(null, new Packet(input), result);
                return (Packet)result[0];
            }
        }
        
//        [Benchmark]
//        public bool Improved()
//        {
//            throw new NotImplementedException();
//        }
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Packet packet = new Packet("eth", 1, message);
            Packet ensnapped = _snappyEncoder.TestEncode(packet.Data);
            List<object> output = new List<object>();
            _splitter.Encode(ensnapped, output);
            _encoder.Encode((byte[])output[0], _byteBuffer);
        }
        
        [Benchmark]
        public void Current_no_snappy()
        {
            Packet packet = new Packet("eth", 1, message);
            List<object> output = new List<object>();
            _splitter.Encode(packet, output);
            _encoder.Encode((byte[])output[0], _byteBuffer);
        }
        
        [Benchmark]
        public void Current_no_splitter()
        {
            Packet packet = new Packet("eth", 1, message);
            Packet ensnapped = _snappyEncoder.TestEncode(packet.Data);
            _encoder.Encode(ensnapped.Data, _byteBuffer);
        }
        
        [Benchmark]
        public void Current_no_encoder()
        {
            Packet packet = new Packet("eth", 1, message);
            Packet ensnapped = _snappyEncoder.TestEncode(packet.Data);
            List<object> output = new List<object>();
            _splitter.Encode(ensnapped, output);
        }
    }
}