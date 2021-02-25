//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public ref struct LogEntriesIterator
    {
        private readonly LogEntry[]? _logs;
        private readonly int _length;
        private Rlp.ValueDecoderContext _decoderContext;
        public long Index { get; private set; }

        public LogEntriesIterator(Span<byte> data)
        {
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            Index = -1;
            _logs = null;
        }

        public LogEntriesIterator(LogEntry[] logs)
        {
            _decoderContext =new Rlp.ValueDecoderContext();
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
                    LogEntryDecoder.DecodeStructRef(ref _decoderContext, RlpBehaviors.None, out current);
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
