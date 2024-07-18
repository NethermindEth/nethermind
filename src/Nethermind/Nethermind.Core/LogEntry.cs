// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class LogEntry
    {
        public LogEntry(Address address, byte[] data, Hash256[] topics)
        {
            LoggersAddress = address;
            Data = data;
            Topics = topics;
        }

        public Address LoggersAddress { get; }
        public Hash256[] Topics { get; }
        public byte[] Data { get; }
    }

    public ref struct LogEntryStructRef
    {
        public LogEntryStructRef(AddressStructRef address, ReadOnlySpan<byte> data, ReadOnlySpan<byte> topicsRlp)
        {
            LoggersAddress = address;
            Data = data;
            TopicsRlp = topicsRlp;
            Topics = null;
        }

        public AddressStructRef LoggersAddress;

        public LogEntryStructRef(LogEntry logEntry)
        {
            LoggersAddress = logEntry.LoggersAddress.ToStructRef();
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
