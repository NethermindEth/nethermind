// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public ref struct KeccaksIterator
    {
        private readonly RlpReader _start;
        private RlpReader _reader;
        private readonly Span<byte> _buffer;
        public long Index { get; private set; }

        public KeccaksIterator(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            if (buffer.Length != 32) throw new ArgumentException("Buffer must be 32 bytes long");
            _reader = new RlpReader(data).EnterSequence<OffFlag>(out _);
            _start = _reader;
            _buffer = buffer;
            Index = -1;
        }

        public bool TryGetNext(out Hash256StructRef current)
        {
            if (_reader.HasNext)
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

        public void Reset() => _reader = _start;
    }
}
