// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
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

        private readonly TxReceipt[]? _receipts;
        private int _position;
        private readonly IReceiptsRecovery.IRecoveryContext? _recoveryContext;
        private readonly bool _compactEncoding;

        public ReceiptsIterator(scoped in Span<byte> receiptsData, IDbWithSpan blocksDb, IReceiptsRecovery.IRecoveryContext? receiptsRecoveryContext)
        {
            _decoderContext = receiptsData.AsRlpValueContext();
            _blocksDb = blocksDb;
            _receipts = null;
            _position = 0;
            _recoveryContext = receiptsRecoveryContext;

            if (_decoderContext.Length > 0 && _decoderContext.PeekByte() == ReceiptArrayStorageDecoder.CompactEncoding)
            {
                _compactEncoding = true;
                _decoderContext.ReadByte();
            }
            else
            {
                _compactEncoding = false;
            }

            _length = receiptsData.Length == 0 ? 0 : _decoderContext.ReadSequenceLength();
        }

        /// <summary>
        /// Note: This code path assume the receipts already have other info recovered. Its used only by cache.
        /// </summary>
        /// <param name="receipts"></param>
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
            if (_receipts is null)
            {
                if (_decoderContext.Position < _length)
                {
                    if (_compactEncoding)
                    {
                        CompactReceiptStorageDecoder.Instance.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                    }
                    else
                    {
                        ReceiptStorageDecoder.Instance.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                    }
                    _recoveryContext?.RecoverReceiptData(ref current);
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
            if (_receipts is not null)
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
            if (_receipts is null && !_decoderContext.Data.IsEmpty)
            {
                _blocksDb?.DangerousReleaseMemory(_decoderContext.Data);
            }
        }
    }
}
