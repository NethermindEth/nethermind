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

using Nethermind.Core.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public class LogEntryBuilder : BuilderBase<LogEntry>
    {
        private Address _address = Address.Zero;
        private byte[] _data = new byte[0];
        private Keccak[] _topics = new [] {Keccak.Zero}; 

        public LogEntryBuilder()
        {
            Build();
        }

        public LogEntryBuilder WithAddress(Address address)
        {
            _address = address;
            
            return Build();
        }

        public LogEntryBuilder WithData(byte[] data)
        {
            _data = data;

            return Build();
        }

        public LogEntryBuilder WithTopics(params Keccak[] topics)
        {
            _topics = topics;
            
            return Build();
        }

        private LogEntryBuilder Build()
        {
            TestObjectInternal = new LogEntry(_address, _data, _topics);
            
            return this;
        }
    }
}