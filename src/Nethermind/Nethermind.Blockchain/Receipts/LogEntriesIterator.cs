// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        private Rlp.ValueDecoderContext _decoderContext;
        private IReceiptRefDecoder _receiptRefDecoder;
        public long Index { get; private set; }

        public LogEntriesIterator(Span<byte> data, IReceiptRefDecoder receiptRefDecoder)
        {
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            Index = -1;
            _logs = null;
            _receiptRefDecoder = receiptRefDecoder;
        }

        public LogEntriesIterator(LogEntry[] logs)
        {
            _decoderContext = new Rlp.ValueDecoderContext();
            _length = logs.Length;
            Index = -1;
            _logs = logs;
        }

        public bool TryGetNext(out LogEntryStructRef current)
        {
            if (_logs is null)
            {
                if (_decoderContext.Position < _length)
                {
                    _receiptRefDecoder.DecodeLogEntryStructRef(ref _decoderContext, RlpBehaviors.None, out current);
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
                _decoderContext.Position = 0;
                _decoderContext.ReadSequenceLength();
            }
        }

        public bool TrySkipNext()
        {
            if (_logs is null)
            {
                if (_decoderContext.Position < _length)
                {
                    _decoderContext.SkipItem();
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
