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
using System.Linq;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    // TODO: most likely inefficient -> compare with net libraries
    public class MessageProcessingPipeline : IMessageProcessingPipeline
    {
        private readonly List<IMessageProcessor> _messageProcessors = new List<IMessageProcessor>();

        public byte[] Publish(object message)
        {
            List<object> input = new List<object> {message};
            List<object> output = new List<object>();
            int i = _messageProcessors.Count - 1;
            do
            {
                output.Clear();
                for (int j = 0; j < input.Count; j++)
                {
                    _messageProcessors[i].ToLeftBase(input[j], output);
                }

                (input, output) = (output, input);
                i--;
            } while (i >= 0);

            // TODO: review
            return Bytes.Concat(input.OfType<byte[]>().ToArray());
        }

        public void ConsumeAll(byte[] bytes)
        {
            List<object> input = new List<object> {bytes};
            List<object> output = new List<object>();
            int i = 0;
            do
            {
                output.Clear();
                for (int j = 0; j < input.Count; j++)
                {
                    _messageProcessors[i].ToRightBase(input[j], output);
                }

                (input, output) = (output, input);
                i++;
            } while (i < _messageProcessors.Count);

            // TODO: handler for messages that were not consumed? 
        }

        public void AddRight(IMessageProcessor messageProcessor)
        {
            _messageProcessors.Add(messageProcessor);
        }
    }
}