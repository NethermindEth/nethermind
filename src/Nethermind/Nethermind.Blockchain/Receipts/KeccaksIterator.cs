// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public ref struct KeccaksIterator
    {
        private readonly int _length;
        private readonly int _startPosition;
        private Rlp.ValueDecoderContext _decoderContext;
        private readonly Span<byte> _buffer;
        public long Index { get; private set; }

        public KeccaksIterator(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            if (buffer.Length != 32) throw new ArgumentException("Buffer must be 32 bytes long");
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            _startPosition = _decoderContext.Position;
            _buffer = buffer;
            Index = -1;
        }

        public bool TryGetNext(out Hash256StructRef current)
        {
            if (_decoderContext.Position < _length + _startPosition)
            {
                _decoderContext.DecodeZeroPrefixedKeccakStructRef(out current, _buffer);
                Index++;
                return true;
            }
            else
            {
                current = new Hash256StructRef(Keccak.Zero.Bytes);
                return false;
            }
        }

        public void Reset()
        {
            _decoderContext.Position = _startPosition;
        }
    }
}
