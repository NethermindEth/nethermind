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
        
        private readonly TxReceipt[] _receipts;
        private int _position;

        public ReceiptsIterator(in Span<byte> receiptsData, IDbWithSpan blocksDb)
        {
            _decoderContext = receiptsData.AsRlpValueContext();
            _length = receiptsData.Length == 0 ? 0 : _decoderContext.ReadSequenceLength();
            _blocksDb = blocksDb;
            _receipts = null;
            _position = 0;
        }

        public ReceiptsIterator(TxReceipt[] receipts)
        {
            _decoderContext = new Rlp.ValueDecoderContext();
            _length = receipts.Length;
            _blocksDb = null;
            _receipts = receipts;
            _position = 0;
        }

        public bool TryGetNext(out TxReceiptStructRef current)
        {
            if (_receipts == null)
            {
                if (_decoderContext.Position < _length)
                {
                    ReceiptStorageDecoder.Instance.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                    return true;
                }
            }
            else
            {
                if (_position < _length)
                {
                    current = new TxReceiptStructRef(_receipts[_position++]);
                    return true;
                }
            }
            
            current = new TxReceiptStructRef();
            return false;
        }

        public void Reset()
        {
            if (_receipts != null)
            {
                _position = 0;
            }
            else
            {
                _decoderContext.Position = 0;
                _decoderContext.ReadSequenceLength();
            }
        }

        public void Dispose()
        {
            if (_receipts == null && !_decoderContext.Data.IsEmpty)
            {
                _blocksDb?.DangerousReleaseMemory(_decoderContext.Data);
            }
        }
    }
}
