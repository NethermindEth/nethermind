//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.IO;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class BlockParameterConverterTests : SerializationTestBase
    {
        [TestCase("0x0", 0)]
        [TestCase("0xA", 10)]
        [TestCase("0xa", 10)]
        [TestCase("0", 0)]
        [TestCase("100", 100)]
        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xA\"", 10)]
        [TestCase("\"0xa\"", 10)]
        [TestCase("\"0\"", 0)]
        [TestCase("\"100\"", 100)]
        public void Can_read_block_number(string input, long output)
        {
            using StringReader reader = new StringReader(input);
            using JsonTextReader textReader = new JsonTextReader(reader);

            JsonSerializer serializer = new JsonSerializer();
            BlockParameterConverter converter = new BlockParameterConverter();
            serializer.Converters.Add(converter);
            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(textReader);
            
            Assert.AreEqual(output, blockParameter.BlockNumber);
        }
        
        [TestCase("null", BlockParameterType.Latest)]
        [TestCase("\"\"", BlockParameterType.Latest)]
        [TestCase("\"latest\"", BlockParameterType.Latest)]
        [TestCase("\"LATEst\"", BlockParameterType.Latest)]
        [TestCase("\"earliest\"", BlockParameterType.Earliest)]
        [TestCase("\"EaRlIEST\"", BlockParameterType.Earliest)]
        [TestCase("\"pending\"", BlockParameterType.Pending)]
        [TestCase("\"PeNdInG\"", BlockParameterType.Pending)]
        public void Can_read_type(string input, BlockParameterType output)
        {
            using StringReader reader = new StringReader(input);
            using JsonTextReader textReader = new JsonTextReader(reader);

            JsonSerializer serializer = new JsonSerializer();
            BlockParameterConverter converter = new BlockParameterConverter();
            serializer.Converters.Add(converter);
            BlockParameter blockParameter = serializer.Deserialize<BlockParameter>(textReader);
            
            Assert.AreEqual(output, blockParameter.Type);
        }
        
        [TestCase("\"latest\"", BlockParameterType.Latest)]
        [TestCase("\"earliest\"", BlockParameterType.Earliest)]
        [TestCase("\"pending\"", BlockParameterType.Pending)]
        [TestCase("null", BlockParameterType.BlockNumber)]
        [TestCase("null", BlockParameterType.BlockHash)]
        public void Can_write_type(string output, BlockParameterType input)
        {
            BlockParameter blockParameter = new BlockParameter(input);
            
            using StringWriter reader = new StringWriter();
            using JsonTextWriter textWriter = new JsonTextWriter(reader);

            JsonSerializer serializer = new JsonSerializer();
            BlockParameterConverter converter = new BlockParameterConverter();
            serializer.Converters.Add(converter);
            serializer.Serialize(textWriter, blockParameter);
            
            Assert.AreEqual(output, reader.ToString());
        }
        
        [TestCase("\"0x0\"", 0)]
        [TestCase("\"0xa\"", 10)]
        public void Can_write_number(string output, long input)
        {
            BlockParameter blockParameter = new BlockParameter(input);
            
            using StringWriter reader = new StringWriter();
            using JsonTextWriter textWriter = new JsonTextWriter(reader);

            JsonSerializer serializer = new JsonSerializer();
            BlockParameterConverter converter = new BlockParameterConverter();
            serializer.Converters.Add(converter);
            serializer.Serialize(textWriter, blockParameter);
            
            Assert.AreEqual(output, reader.ToString());
        }
        
        [Test]
        public void Can_do_roundtrip()
        {
            TestRoundtrip(BlockParameter.Latest, "latest");
            TestRoundtrip(BlockParameter.Pending, "pending");
            TestRoundtrip(BlockParameter.Earliest, "earliest");
            TestRoundtrip(new BlockParameter(0L), "zero");
            TestRoundtrip(new BlockParameter(long.MaxValue), "max");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA), "hash");
            TestRoundtrip(new BlockParameter(TestItem.KeccakA, true), "hash with canonical");
        }
    }
}
