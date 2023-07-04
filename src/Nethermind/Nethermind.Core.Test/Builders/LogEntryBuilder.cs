// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public class LogEntryBuilder : BuilderBase<LogEntry>
    {
        private Address _address = Address.Zero;
        private byte[] _data = Array.Empty<byte>();
        private Keccak[] _topics = new[] { Keccak.Zero };

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
