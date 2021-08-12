//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using DotNetty.Common;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;

namespace Nethermind.Network.Benchmarks
{
    public class InFlowBenchmarks
    {
        private static byte[] _input = Bytes.FromHexString("96cb6a910a279cab5bf0e0cd0432c7d1d28f76b794402bd97ba5a7dfa1b0e163fc75cd604bbc43d3c888618db2453158b06548d9bdb1b8208b70e0075d675bbe80268afff07570005454467c0c3e85a3468e3225015521fecb9a0c1a2092fd445f14552e33d4ebb4d7b533606989210b4c702662b4d2417f293b2701ee4336624916cddefb4c3777d1a144e1df66162b60bcaed3319becbbe19062f7e3715505dc84fa23a531d7dfd35b9ad95042a85faa643a40365d52d0b57e67cc428676cc364567a6c8cf7fda48bd4c54fd2c8fad1e504f3c2451f538793c022fcdfaef9d8fdafae2c989a7e61dba88879cc9062b774bc3f999b0cdece9aab3d34fafc1513e2de18e6c297d07a5992cc68adb27ce3de39c290884e2f6f0f8b24645c622b2cd9d396498e689a44dc775c22f4baa6b5c1685c19b020110c5788b0e61ebf1794f05df9237d61ca95f49f00fa0217c6a0935f5674c32f8eb7b9c5fe351d429ba0383761d980b53b1187a9a367f80075c020fcc71a75689a4a9e74b8c6137ab267ce6e697008ff5f8c29af53fd4f97018da197f990181f112be738fd0f57bcfc72a78dda731b64aecdd3f83586f1fcfdbdf2c58eff42b38fed19730a1fd0dd21d248d78a396689a508561590543dbe62b44475bec724f2c13678dc877d6cc15099634c10ee4206b339e45c4829c8d81537c3b57fab792929f79a391c29d4f10f6db8318d9f2bffffdafb3038f005ab5e9ce4bf46960f7cf078cebc4287da0330f6a02555912ce7db52a893e4739af43dd64eb9a70e8b4460da9955c39e5c24d2d35b29999b4c9979ba5acd16abd09427b57ad78db5076b0671fa4bf90a48b863db8a372c99e28a21213246f15cf36470f3a397b9e3cd1b0e4ce4e9ff1f95c5bc4c63c691a960d12e45f452adc7961eaca8dd24a3f51b16c6b15c930500b3c9b9e99638eedbf18332de4f5fa3e948872f46bbe2594de8bf32a1ca36a80a284dc1e055379543bded00cf110bdf5da82f0dc5e5a410c5aa925dd4a7dd2408796f05d3f1ab43c5b30a17e7e392212b0a61eacdfb200a1788e99f85a896e77fe7a86a17e61fdf00da6d9b2aac1935068bb6271a265e6b41aca2662b9beca7005bbbcbb371bb7daf60bf3be282e67ba17cb7d120015a8f7867391197c457e913a9558382f4dfbdd1e8693babf5835707c0142be013ec92869415c772dfc4581c5bb0aaaf0f7dabee7d488ea7e8a8954507e7f04f942429be3dda2317f719f8cf8e68720a91cce20d2406f35347cadb24e4ce3024a70c7938fa14179a9434996f73d666bd9fcb9b0dc02492fdef10a52c39a976058d611d854e6cad01cf85ff380499909638f7ca8f7397cb52b7d3cd9401b5905838b6134e6dbf4a03dff05551043532dbe9833502bba97839964806d55655804eef2317f68caf724f28625bcae7b761fac920d256c78e3843529e4ce7d25861db489128112002df79ee13cdcf5776e0416bdb36c022deba8d5329900da5c9a3bac6d39284691ca2ee684ee7fc9335c5183542b3751199587e9a0a0cad9b7e5365703ceda47d5959548a6a097fe8ff0f1fe8c59ad377db05f11002bacf555d14e99d7ae8cfd7960967384d912ecc3e11cc24cf9849d2592b834fa04ce12e0cb75eb4668ad6d3e43f16cf0dc2c9f54d77877014859b3b74b2bf9489b06682e0bd8f868a7fc34d8e7ff7d2c03435c5a15cc61a0a0791db38a9cc205e9f18e28e1445bfe982e25aeaa196d8dd14d25b65add73760726e15a1f54a414fd9377d2f87d33f87e117731fa6d04377686f33578c8b5481580fd81a74a17e4fb011bdcd9a10ee65ca37ae9eb5e606fde06d95d50880f4732ea6cb81f3d9adc88d43aa15aeadd4e790973f88b0dcdbe18cab73a9309f9e151c21d4025aa1af0fb37fe31ce25ce39c51fdb4e932b45c168111f54398fea71dd3e82ab01499db695e36308bc800246f17393d218a241191086fa02a4d70514e5bb3c5ff229a8320e53e0cd2b605c674dd6006b74a394fcfcf4d8fe0b61c3768b9e53b6601854ebe5dcd210b3db16adf68065a4f60a7dbd3c79776a21f6440cdc263aaa423b11954795d9424e3543b27efbe8dd76e002c30889e872c6d12d82c48933c382347ee89e69cf5d0120b342969312f98a19a8e972a8ef4f78a23701ac80deaf90cbc6b13fddea674790d6e9fe46d054f900b569e624bd50f8c8b20d3cdb29117f0cd28cb3a15c8530fb4827ebd3af739e872b59e6566c928933");

        private IByteBuffer _decoderBuffer = PooledByteBufferAllocator.Default.Buffer(1024 * 1024);
        private NewBlockMessage _outputMessage;

        private NewBlockMessageSerializer _newBlockMessageSerializer;
        private Block _block;
        private TestZeroMerger _zeroMerger;
        private TestZeroDecoder _zeroDecoder;
//        private TestZeroSnappy _zeroSnappyEncoder;
        private NewBlockMessage _newBlockMessage;
        private MessageSerializationService _serializationService;
        private (EncryptionSecrets A, EncryptionSecrets B) _secrets;

        [GlobalSetup]
        public void Setup()
        {
            if (_decoderBuffer.ReadableBytes > 0)
            {
                throw new Exception("decoder buffer");
            }
            
            SetupAll();
            IterationSetup();
            Current();
            Check();
//            SetupAll();
//            Improved();
//            Check();

            SetupAll();
            IterationSetup();
        }
        
        [IterationSetup]
        public void IterationSetup()
        {
            _secrets = NetTestVectors.GetSecretsPair();
            FrameCipher frameCipher = new FrameCipher(_secrets.B.AesSecret);
            FrameMacProcessor frameMacProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, _secrets.B);
            _zeroDecoder = new TestZeroDecoder(frameCipher, frameMacProcessor);
        }

        private void SetupAll(bool useLimboOutput = false)
        {
            _zeroMerger = new TestZeroMerger();
//            _zeroSnappyEncoder = new TestZeroSnappy();
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            _block = Build.A.Block.WithTransactions(a, b).TestObject;
            _newBlockMessageSerializer = new NewBlockMessageSerializer();

            _newBlockMessage = new NewBlockMessage();
            _newBlockMessage.Block = _block;
            _serializationService = new MessageSerializationService();
            _serializationService.Register(_newBlockMessageSerializer);
            ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Paranoid;
        }

        private class TestZeroDecoder : ZeroFrameDecoder
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

        private class TestZeroMerger : Rlpx.ZeroFrameMerger
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
            if (_outputMessage.Block.Transactions.Length != 10)
            {
                throw new Exception(_outputMessage.Block.Transactions.Length.ToString());
            }
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _decoderBuffer.WriteBytes(_input);
            IByteBuffer decoded = _zeroDecoder.Decode(_decoderBuffer);
            IByteBuffer merged = _zeroMerger.Decode(decoded);
            merged.ReadByte();
            _outputMessage = _newBlockMessageSerializer.Deserialize(merged.ReadAllBytes());
        }
    }
}
