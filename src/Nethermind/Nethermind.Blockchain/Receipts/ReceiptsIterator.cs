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
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
#pragma warning disable 618

namespace Nethermind.Blockchain.Receipts
{
    public ref struct ReceiptsIterator
    {
        private readonly IDbWithSpan _blocksDb;
        private readonly int _length;
        private Rlp.ValueDecoderContext _decoderContext;

        public ReceiptsIterator(in Span<byte> receiptsData, IDbWithSpan blocksDb)
        {
            _decoderContext = new Rlp.ValueDecoderContext(receiptsData);
            _blocksDb = blocksDb;
            _length = _decoderContext.ReadSequenceLength();;
        }

        public bool TryGetNext(out TxReceiptStructRef current)
        {
            if (_decoderContext.Position < _length)
            {
                ReceiptStorageDecoder.Instance.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                return true;
            }
            else
            {
                current = new TxReceiptStructRef();
                return false;
            }
        }

        public void Reset()
        {
            _decoderContext.Position = 0;
            _decoderContext.ReadSequenceLength();
        }

        public void Dispose()
        {
            _blocksDb.DangerousReleaseMemory(_decoderContext.Data);
        }
    }
}