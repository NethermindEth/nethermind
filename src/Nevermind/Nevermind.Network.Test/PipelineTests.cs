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

using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class PipelineTests
    {
        public class TestProcessorA : MessageProcessorBase<byte[], string>
        {
            public override void ToRight(byte[] input, IList<string> output)
            {
                output.Add(new Hex(input).ToString(true));
            }

            public override void ToLeft(string input, IList<byte[]> output)
            {
                output.Add(new Hex(input));
            }
        }

        public class TestProcessorB : MessageProcessorBase<string, string>
        {
            public override void ToRight(string input, IList<string> output)
            {
                output.Add(input.ToUpperInvariant());
            }

            public override void ToLeft(string input, IList<string> output)
            {
                output.Add(input.ToLowerInvariant());
            }
        }

        public class TestConsumer : MessageConsumerBase<string>
        {
            public List<string> Consumed { get; set; } = new List<string>();

            protected override bool Consume(string input)
            {
                Consumed.Add(input);
                return true;
            }
        }

        [Test]
        public void Consume_works()
        {
            List<string> expectedResult = new List<string> {"0X00"};
            MessageProcessingPipeline messageProcessingPipeline = new MessageProcessingPipeline();
            messageProcessingPipeline.AddRight(new TestProcessorA());
            messageProcessingPipeline.AddRight(new TestProcessorB());
            TestConsumer consumer = new TestConsumer();
            messageProcessingPipeline.AddRight(consumer);
            messageProcessingPipeline.ConsumeAll(new byte[] {0});

            Assert.AreEqual(expectedResult, consumer.Consumed);
        }

        [Test]
        public void To_left_works()
        {
            string rightBefore = "0X00";
            MessageProcessingPipeline messageProcessingPipeline = new MessageProcessingPipeline();
            messageProcessingPipeline.AddRight(new TestProcessorA());
            messageProcessingPipeline.AddRight(new TestProcessorB());
            messageProcessingPipeline.AddRight(new TestConsumer());

            byte[] bytes = messageProcessingPipeline.Publish(rightBefore);
            Assert.AreEqual(new byte[] {0}, bytes);
        }
    }
}