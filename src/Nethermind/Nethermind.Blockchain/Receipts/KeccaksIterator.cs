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
        private RlpReader _reader;
        private readonly Span<byte> _buffer;
        public long Index { get; private set; }

        public KeccaksIterator(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            if (buffer.Length != 32) throw new ArgumentException("Buffer must be 32 bytes long");
            _reader = new RlpReader(data);
            _length = _reader.ReadSequenceLength();
            _startPosition = _reader.Position;
            _buffer = buffer;
            Index = -1;
        }

        public bool TryGetNext(out Hash256StructRef current)
        {
            if (_reader.Position < _length + _startPosition)
            {
                _reader.DecodeZeroPrefixedKeccakStructRef(out current, _buffer);
                Index++;
                return true;
            }
            else
            {
                current = new Hash256StructRef(Keccak.Zero.Bytes);
                return false;
            }
        }

        public void Reset() => _reader.Position = _startPosition;
    }
}
