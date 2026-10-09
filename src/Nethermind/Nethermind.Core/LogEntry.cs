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
        private ReadOnlySpan<byte> _data;
        private int _dataZeroPrefix;

        public LogEntryStructRef(AddressStructRef address, ReadOnlySpan<byte> data, ReadOnlySpan<byte> topicsRlp)
            : this(address, data, topicsRlp, 0)
        {
        }

        internal LogEntryStructRef(AddressStructRef address, ReadOnlySpan<byte> data, ReadOnlySpan<byte> topicsRlp, int dataZeroPrefix)
        {
            Address = address;
            _data = data;
            _dataZeroPrefix = dataZeroPrefix;
            TopicsRlp = topicsRlp;
            Topics = null;
        }

        public AddressStructRef Address;

        public LogEntryStructRef(LogEntry logEntry)
        {
            Address = logEntry.Address.ToStructRef();
            _data = logEntry.Data;
            _dataZeroPrefix = 0;
            Topics = logEntry.Topics;
            TopicsRlp = default;
        }

        public Hash256[]? Topics { get; }

        /// <summary>
        /// Rlp encoded array of Keccak
        /// </summary>
        public ReadOnlySpan<byte> TopicsRlp { get; }

        public readonly int DataLength => _dataZeroPrefix + _data.Length;

        public readonly void CopyDataTo(Span<byte> destination)
        {
            destination = destination[..DataLength];
            _data.CopyTo(destination[_dataZeroPrefix..]);
            destination[.._dataZeroPrefix].Clear();
        }

        public ReadOnlySpan<byte> Data
        {
            get
            {
                if (_dataZeroPrefix != 0)
                {
                    byte[] data = new byte[DataLength];
                    CopyDataTo(data);
                    _data = data;
                    _dataZeroPrefix = 0;
                }

                return _data;
            }
        }
    }
}
