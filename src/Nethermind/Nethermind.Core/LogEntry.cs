// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class LogEntry(Address address, byte[] data, Hash256[] topics) : ILogEntry
    {
        public Address Address { get; } = address;
        public Hash256[] Topics { get; } = topics;
        public byte[] Data { get; } = data;
    }

    public ref struct LogEntryStructRef
    {
        public LogEntryStructRef(AddressStructRef address, ReadOnlySpan<byte> data, ReadOnlySpan<byte> topicsRlp)
        {
            Address = address;
            Data = data;
            TopicsRlp = topicsRlp;
            Topics = null;
        }

        public AddressStructRef Address;

        public LogEntryStructRef(LogEntry logEntry)
        {
            Address = logEntry.Address.ToStructRef();
            Data = logEntry.Data;
            Topics = logEntry.Topics;
            TopicsRlp = default;
        }

        public Hash256[]? Topics { get; }

        /// <summary>
        /// Rlp encoded array of Keccak
        /// </summary>
        public ReadOnlySpan<byte> TopicsRlp { get; }

        public ReadOnlySpan<byte> Data { get; }
    }
}
