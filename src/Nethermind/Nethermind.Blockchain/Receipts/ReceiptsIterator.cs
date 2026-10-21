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
        private RlpReader _reader;
        private readonly int _startingPosition;

        private readonly TxReceipt[]? _receipts;
        private int _receiptIndex;

        private readonly Func<IReceiptsRecovery.IRecoveryContext>? _recoveryContextFactory;
        private readonly Func<IReceiptsRecovery.IRecoveryContext>? _logRecoveryContextFactory;
        private IReceiptsRecovery.IRecoveryContext? _recoveryContext;
        private IReceiptsRecovery.ISelectiveLogRecoveryContext? _selectiveLogRecoveryContext;
        private readonly IReceiptsRecovery.IDeferredLogRecoveryContext? _deferredRecoveryContext;
        private int _logRecoveryReceiptIndex;
        private Hash256? _logRecoveryTransactionHash;
        private readonly IReceiptRefDecoder _receiptRefDecoder;
        private bool _recoveryContextConfigured;
        private readonly ReadOnlySpan<byte> _txHashes;
        private readonly Hash256? _blockHash;
        private readonly ulong _blockNumber;

        public ReceiptsIterator(scoped in Span<byte> receiptsData, IDb blocksDb, Func<IReceiptsRecovery.IRecoveryContext?>? recoveryContextFactory, IReceiptRefDecoder receiptRefDecoder,
            Func<IReceiptsRecovery.IRecoveryContext?>? logRecoveryContextFactory = null)
        {
            RlpReader reader = new(receiptsData);
            ReceiptArrayStorageDecoder.TrySkipCompactPrefix(ref reader, out ReadOnlySpan<byte> txHashes);
            _reader = reader;
            _txHashes = txHashes;
            _blocksDb = blocksDb;
            _receipts = null;
            _receiptIndex = 0;
            _recoveryContextFactory = recoveryContextFactory;
            _logRecoveryContextFactory = logRecoveryContextFactory ?? recoveryContextFactory;
            _recoveryContextConfigured = false;
            _recoveryContext = null;
            _receiptRefDecoder = receiptRefDecoder;

            _startingPosition = _reader.Position;
            _length = receiptsData.Length == 0 ? 0 : _reader.ReadSequenceLength() + _reader.Position;
        }

        internal ReceiptsIterator(scoped in Span<byte> receiptsData, IDb blocksDb, IReceiptRefDecoder receiptRefDecoder,
            IReceiptsRecovery.IDeferredLogRecoveryContext recoveryContext, Hash256? blockHash = null, ulong blockNumber = 0)
            : this(receiptsData, blocksDb, null, receiptRefDecoder)
        {
            _deferredRecoveryContext = recoveryContext;
            _selectiveLogRecoveryContext = recoveryContext;
            _blockHash = blockHash;
            _blockNumber = blockNumber;
        }

        /// <summary>
        /// Note: This code path assume the receipts already have other info recovered. Its used only by cache.
        /// </summary>
        /// <param name="receipts"></param>
        public ReceiptsIterator(TxReceipt[] receipts)
        {
            _reader = new RlpReader();
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
                if (_reader.Position < _length)
                {
                    if (_reader.IsNextItemEmptyList())
                    {
                        _reader.Position = _length;
                        current = default;
                        return false;
                    }

                    _receiptRefDecoder.DecodeStructRef(ref _reader, RlpBehaviors.Storage, out current);
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

        /// <summary>Returns how many receipts are left to iterate, reading only their RLP length prefixes.</summary>
        public readonly int CountRemaining() =>
            _receipts is null ? _reader.PeekNumberOfItemsRemaining(_length) : _length - _receiptIndex;

        /// <summary>Advances past the next <paramref name="count"/> receipts without decoding them.</summary>
        /// <remarks>Must not be called once recovery is configured: skipped receipts would not reach the recovery context.</remarks>
        public void Skip(int count)
        {
            if (_recoveryContext is not null) throw new InvalidOperationException("Cannot skip receipts while recovering them.");
            if (_receipts is null) _reader.SkipItems(count);
            _receiptIndex += count;
        }

        public void RecoverIfNeeded(ref TxReceiptStructRef current) => RecoverIfNeeded(ref current, _recoveryContextFactory);

        /// <summary>
        /// Recovers only the block hash and number, transaction index and transaction hash, which is all a log carries;
        /// the other receipt fields stay as decoded for the rest of the iteration.
        /// </summary>
        public void RecoverLogFieldsIfNeeded(ref TxReceiptStructRef current)
        {
            RecoverLogFieldsIfNeeded(ref current, out Hash256? transactionHash);
            if (transactionHash is not null) current.TxHash = transactionHash.ToStructRef();
        }

        public void RecoverLogFieldsIfNeeded(ref TxReceiptStructRef current, out Hash256? transactionHash)
        {
            transactionHash = null;
            if (_recoveryContextConfigured) return;
            if (_receiptIndex == _logRecoveryReceiptIndex)
            {
                transactionHash = _logRecoveryTransactionHash;
                return;
            }

            if (_blockHash is not null && !_txHashes.IsEmpty)
            {
                transactionHash = _logRecoveryTransactionHash = RecoverStoredLogFields(ref current, _receiptIndex - 1);
                _logRecoveryReceiptIndex = _receiptIndex;
                return;
            }

            if (_selectiveLogRecoveryContext is null)
            {
                IReceiptsRecovery.IRecoveryContext? context = _logRecoveryContextFactory?.Invoke();
                if (context is not IReceiptsRecovery.ISelectiveLogRecoveryContext selective)
                {
                    ConfigureRecovery(ref current, context);
                    return;
                }

                _selectiveLogRecoveryContext = selective;
            }

            transactionHash = _logRecoveryTransactionHash = _selectiveLogRecoveryContext!.RecoverReceiptData(ref current, _receiptIndex - 1);
            _logRecoveryReceiptIndex = _receiptIndex;
        }

        private readonly Hash256 RecoverStoredLogFields(ref TxReceiptStructRef current, int transactionIndex)
        {
            int offset = transactionIndex * Hash256.Size;
            if ((uint)offset > (uint)(_txHashes.Length - Hash256.Size))
            {
                throw new InvalidOperationException($"Receipt {transactionIndex} has no stored transaction hash, the block stores {_txHashes.Length / Hash256.Size}.");
            }

            current.BlockHash = _blockHash!.ToStructRef();
            current.BlockNumber = _blockNumber;
            current.Index = transactionIndex;
            return new Hash256(_txHashes.Slice(offset, Hash256.Size));
        }

        private void RecoverIfNeeded(ref TxReceiptStructRef current, Func<IReceiptsRecovery.IRecoveryContext?>? recoveryContextFactory)
        {
            if (_recoveryContextConfigured) return;

            ConfigureRecovery(ref current, _deferredRecoveryContext is null
                ? recoveryContextFactory?.Invoke()
                : _deferredRecoveryContext.CreateFullRecoveryContext());
        }

        private void ConfigureRecovery(ref TxReceiptStructRef current, IReceiptsRecovery.IRecoveryContext? context)
        {
            _recoveryContext = context;
            if (_recoveryContext is not null)
            {
                // Need to replay the context.
                _reader.Position = _startingPosition;
                if (_length != 0) _reader.ReadSequenceLength();
                for (int i = 0; i < _receiptIndex; i++)
                {
                    _receiptRefDecoder.DecodeStructRef(ref _reader, RlpBehaviors.Storage, out current);
                    _recoveryContext?.RecoverReceiptData(ref current);
                }
            }

            _recoveryContextConfigured = true;
        }

        public readonly void Dispose()
        {
            if (_receipts is null && !_reader.Data.IsEmpty)
            {
                _blocksDb?.DangerousReleaseMemory(_reader.Data);
            }
            _recoveryContext?.Dispose();
            _selectiveLogRecoveryContext?.Dispose();
        }

        public readonly LogEntriesIterator IterateLogs(TxReceiptStructRef receipt) =>
            receipt.Logs is null
                ? new LogEntriesIterator(receipt.LogsRlp, _receiptRefDecoder)
                : new LogEntriesIterator(receipt.Logs);

        public readonly Hash256[] DecodeTopics(RlpReader reader) =>
            _receiptRefDecoder.DecodeTopics(reader);

        public readonly bool CanDecodeBloom => _receiptRefDecoder is null || _receiptRefDecoder.CanDecodeBloom;
    }
}
