// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        private IReceiptRefDecoder _receiptRefDecoder;

        public ReceiptsIterator(scoped in Span<byte> receiptsData, IDbWithSpan blocksDb, IReceiptsRecovery.IRecoveryContext? receiptsRecoveryContext, IReceiptRefDecoder receiptRefDecoder)
        {
            _decoderContext = receiptsData.AsRlpValueContext();
            _blocksDb = blocksDb;
            _receipts = null;
            _position = 0;
            _recoveryContext = receiptsRecoveryContext;
            _receiptRefDecoder = receiptRefDecoder;

            if (_decoderContext.Length > 0 && _decoderContext.PeekByte() == ReceiptArrayStorageDecoder.CompactEncoding)
            {
                _decoderContext.ReadByte();
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
                    _receiptRefDecoder.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
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

        public LogEntriesIterator IterateLogs(TxReceiptStructRef receipt)
        {
            return receipt.Logs is null ? new LogEntriesIterator(receipt.LogsRlp, _receiptRefDecoder) : new LogEntriesIterator(receipt.Logs);
        }

        public Keccak[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return _receiptRefDecoder.DecodeTopics(valueDecoderContext);
        }

        public bool CanDecodeBloom => _receiptRefDecoder == null || _receiptRefDecoder.CanDecodeBloom;
    }
}
