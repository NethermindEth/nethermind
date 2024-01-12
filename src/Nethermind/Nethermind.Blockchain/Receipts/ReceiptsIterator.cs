// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
#pragma warning disable 618

namespace Nethermind.Blockchain.Receipts
{
    public ref struct ReceiptsIterator
    {
        private readonly IDb _blocksDb;
        private readonly int _length;
        private Rlp.ValueDecoderContext _decoderContext;
        private readonly int _startingPosition;

        private readonly TxReceipt[]? _receipts;
        private int _receiptIndex;

        private readonly Func<IReceiptsRecovery.IRecoveryContext>? _recoveryContextFactory;
        private IReceiptsRecovery.IRecoveryContext? _recoveryContext;
        private readonly IReceiptRefDecoder _receiptRefDecoder;
        private bool _recoveryContextConfigured;

        public ReceiptsIterator(scoped in Span<byte> receiptsData, IDb blocksDb, Func<IReceiptsRecovery.IRecoveryContext?>? recoveryContextFactory, IReceiptRefDecoder receiptRefDecoder)
        {
            _decoderContext = receiptsData.AsRlpValueContext();
            _blocksDb = blocksDb;
            _receipts = null;
            _receiptIndex = 0;
            _recoveryContextFactory = recoveryContextFactory;
            _recoveryContextConfigured = false;
            _recoveryContext = null;
            _receiptRefDecoder = receiptRefDecoder;

            if (_decoderContext.Length > 0 && _decoderContext.PeekByte() == ReceiptArrayStorageDecoder.CompactEncoding)
            {
                _decoderContext.ReadByte();
            }

            _startingPosition = _decoderContext.Position;
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
            _receiptIndex = 0;
            _recoveryContextConfigured = true;
        }

        public bool TryGetNext(out TxReceiptStructRef current)
        {
            if (_receipts is null)
            {
                if (_decoderContext.Position < _length)
                {
                    _receiptRefDecoder.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                    _recoveryContext?.RecoverReceiptData(ref current);
                    _receiptIndex++;
                    return true;
                }
            }
            else
            {
                if (_receiptIndex < _length)
                {
                    current = new TxReceiptStructRef(_receipts[_receiptIndex++]);
                    return true;
                }
            }

            current = new TxReceiptStructRef();
            return false;
        }

        public void RecoverIfNeeded(ref TxReceiptStructRef current)
        {
            if (_recoveryContextConfigured) return;

            _recoveryContext = _recoveryContextFactory?.Invoke();
            if (_recoveryContext != null)
            {
                // Need to replay the context.
                _decoderContext.Position = _startingPosition;
                if (_length != 0) _decoderContext.ReadSequenceLength();
                for (int i = 0; i < _receiptIndex; i++)
                {
                    _receiptRefDecoder.DecodeStructRef(ref _decoderContext, RlpBehaviors.Storage, out current);
                    _recoveryContext?.RecoverReceiptData(ref current);
                }
            }

            _recoveryContextConfigured = true;
        }

        public readonly void Dispose()
        {
            if (_receipts is null && !_decoderContext.Data.IsEmpty)
            {
                _blocksDb?.DangerousReleaseMemory(_decoderContext.Data);
            }
            _recoveryContext?.Dispose();
        }

        public readonly LogEntriesIterator IterateLogs(TxReceiptStructRef receipt)
        {
            return receipt.Logs is null ? new LogEntriesIterator(receipt.LogsRlp, _receiptRefDecoder) : new LogEntriesIterator(receipt.Logs);
        }

        public readonly Hash256[] DecodeTopics(Rlp.ValueDecoderContext valueDecoderContext)
        {
            return _receiptRefDecoder.DecodeTopics(valueDecoderContext);
        }

        public readonly bool CanDecodeBloom => _receiptRefDecoder == null || _receiptRefDecoder.CanDecodeBloom;
    }
}
