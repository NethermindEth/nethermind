// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public readonly ref struct KeccakIteratorRef
    {
        public readonly ref readonly ValueKeccak Keccak;

        public KeccakIteratorRef(in ValueKeccak keccak)
        {
            Keccak = ref keccak;
        }
    }

    public ref struct KeccaksIterator
    {
        private readonly int _length;
        private Rlp.ValueDecoderContext _decoderContext;
        public long Index { get; private set; }

        public KeccaksIterator(Span<byte> data)
        {
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            Index = -1;
        }

        public bool TryGetNext(out KeccakIteratorRef current)
        {
            if (_decoderContext.Position < _length)
            {
                current = new KeccakIteratorRef(in _decoderContext.DecodeKeccakStructRef());
                Index++;
                return true;
            }
            else
            {
                current = new KeccakIteratorRef(in Keccak.Zero.ToStructRef());
                return false;
            }
        }

        public void Reset()
        {
            _decoderContext.Position = 0;
            _decoderContext.ReadSequenceLength();
        }
    }
}
