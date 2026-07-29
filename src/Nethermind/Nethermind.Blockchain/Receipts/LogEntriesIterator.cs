// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public ref struct LogEntriesIterator
    {
        private readonly LogEntry[]? _logs;
        private readonly int _length;
        private RlpReader _reader;
        private readonly IReceiptRefDecoder _receiptRefDecoder;
        public long Index { get; private set; }

        public LogEntriesIterator(ReadOnlySpan<byte> data, IReceiptRefDecoder receiptRefDecoder)
        {
            _reader = new RlpReader(data);
            _length = _reader.ReadSequenceLength();
            Index = -1;
            _logs = null;
            _receiptRefDecoder = receiptRefDecoder;
        }

        public LogEntriesIterator(LogEntry[] logs)
        {
            _reader = new RlpReader();
            _length = logs.Length;
            Index = -1;
            _logs = logs;
        }

        public bool TryGetNext(out LogEntryStructRef current)
        {
            if (_logs is null)
            {
                if (_reader.Position < _length)
                {
                    _receiptRefDecoder.DecodeLogEntryStructRef(ref _reader, RlpBehaviors.None, out current);
                    Index++;
                    return true;
                }
            }
            else
            {
                if (++Index < _length)
                {
                    current = new LogEntryStructRef(_logs[Index]);
                    return true;
                }
            }

            current = new LogEntryStructRef();
            return false;
        }

        public void Reset()
        {
            Index = -1;

            if (_logs is null)
            {
                _reader.Position = 0;
                _reader.ReadSequenceLength();
            }
        }

        public bool TrySkipNext()
        {
            if (_logs is null)
            {
                if (_reader.Position < _length)
                {
                    _reader.SkipItem();
                    Index++;
                    return true;
                }
            }
            else
            {
                if (++Index < _length)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
