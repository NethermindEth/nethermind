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
        private Rlp.ValueDecoderContext _decoderContext;
        public long Index { get; private set; }

        public KeccaksIterator(Span<byte> data)
        {
            _decoderContext = new Rlp.ValueDecoderContext(data);
            _length = _decoderContext.ReadSequenceLength();
            Index = -1;
        }

        public bool TryGetNext(out KeccakStructRef current)
        {
            if (_decoderContext.Position < _length)
            {
                _decoderContext.DecodeKeccakStructRef(out current);
                Index++;
                return true;
            }
            else
            {
                current = new KeccakStructRef(Keccak.Zero.Bytes);
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
