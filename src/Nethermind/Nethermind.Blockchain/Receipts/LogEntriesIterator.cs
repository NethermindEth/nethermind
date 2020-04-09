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
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public ref struct LogEntriesIterator
    {
        private readonly int _length;
        private Rlp.ValueDecoderContext _decoderContext;
        public long Index { get; private set; }

        public LogEntriesIterator(Span<byte> data)
        {
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            Index = -1;
        }

        public bool TryGetNext(out LogEntryStructRef current)
        {
            if (_decoderContext.Position < _length)
            {
                LogEntryDecoder.Instance.DecodeStructRef(ref _decoderContext, RlpBehaviors.None, out current);
                Index++;
                return true;
            }
            else
            {
                current = new LogEntryStructRef();
                return false;
            }
        }
        
        public void Reset()
        {
            _decoderContext.Position = 0;
            _decoderContext.ReadSequenceLength();
        }

        public bool TrySkipNext()
        {
            if (_decoderContext.Position < _length)
            {
                _decoderContext.SkipItem();
                Index++;
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}