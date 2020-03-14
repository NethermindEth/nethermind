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

using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Specs;

namespace Nethermind.Network.Benchmarks
{
    [MemoryDiagnoser]
    // [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [ShortRunJob]
    public class NwBlockMessageSerializerBenchmarks
    {
        private NewBlockMessageSerializer _serializer;
        private byte[] _serialized;
        private IByteBuffer _buffer;

        [GlobalSetup]
        public void SetUp()
        {
            _serializer = new NewBlockMessageSerializer();
            ILogManager logManager = ForBenchmarks.Instance;
            var ecdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, logManager);
            Transaction tx1 = Build.A.Transaction.WithData(new byte[1000]).SignedAndResolved(ecdsa, TestItem.PrivateKeyA, 1).TestObject;
            Transaction tx2 = Build.A.Transaction.WithData(new byte[5000]).SignedAndResolved(ecdsa, TestItem.PrivateKeyA, 1).TestObject;
            Transaction tx3 = Build.A.Transaction.WithData(new byte[20]).SignedAndResolved(ecdsa, TestItem.PrivateKeyA, 1).TestObject;
            Transaction tx4 = Build.A.Transaction.WithData(new byte[600]).SignedAndResolved(ecdsa, TestItem.PrivateKeyA, 1).TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(tx1, tx2, tx3, tx4).TestObject;
            NewBlockMessage newBlockMessage = new NewBlockMessage {Block = block, TotalDifficulty = UInt256.One};
            _serialized = _serializer.Serialize(newBlockMessage);
            _buffer = UnpooledByteBufferAllocator.Default.Buffer(_serialized.Length);
            _buffer.WriteBytes(_serialized);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
        }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            _serializer.DeserializeNoCache(_buffer);
            _buffer.SetReaderIndex(0);
        }
        
        [Benchmark]
        public void WithCache()
        {
            _serializer.Deserialize(_buffer);
            _buffer.SetReaderIndex(0);
        }
    }
}