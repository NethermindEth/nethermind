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
using System.Text;

namespace Nethermind.Peering.Mothra
{
    public class GossipReceivedEventArgs : EventArgs
    {
        private string? _topic;

        public GossipReceivedEventArgs(byte[] topicUtf8, byte[] data)
        {
            TopicUtf8 = topicUtf8;
            Data = data;
        }

        public byte[] Data { get; }

        public string Topic
        {
            get
            {
                if (_topic == null)
                {
                    _topic = Encoding.UTF8.GetString(TopicUtf8);
                }

                return _topic;
            }
        }

        public byte[] TopicUtf8 { get; }
    }
}