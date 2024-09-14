// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Blockchain.Find;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using Nethermind.Shutter.Contracts;
using Nethermind.Shutter.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using System.IO;
using Nethermind.Abi;
using Nethermind.Facade.Find;

namespace Nethermind.Shutter;

using G1 = Bls.P1;

public class ShutterTxLoader(
    ILogFinder logFinder,
    IShutterConfig cfg,
    ShutterTime time,
    ISpecProvider specProvider,
    IEthereumEcdsa ecdsa,
    IAbiEncoder abiEncoder,
    ILogManager logManager)
{
    private readonly ShutterLogScanner _logScanner = new(
                new(new Address(cfg.SequencerContractAddress!)),
                logFinder,
                logManager,
                abiEncoder);

    private readonly ShutterEventQueue _events = new(cfg.EncryptedGasLimit, logManager);
    private ulong _txPointer = ulong.MaxValue;
    private bool _loadFromReceipts = false;
    private readonly int _maxTransactions = cfg.EncryptedGasLimit / 21000;
    private readonly ShutterTxFilter _txFilter = new(specProvider, logManager);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public ShutterTransactions LoadTransactions(Block? head, BlockHeader parentHeader, IShutterKeyValidator.ValidatedKeys keys)
    {
        using ArrayPoolList<SequencedTransaction> sequencedTransactions = new(_maxTransactions, GetNextTransactions(keys.Eon, keys.TxPointer, head?.Number ?? 0));

        long offset = time.GetCurrentOffsetMs(keys.Slot);
        Metrics.ShutterKeysReceivedTimeOffset = offset;
        string offsetText = offset < 0 ? $"{-offset}ms before" : $"{offset}ms fter";
        if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count} encrypted transactions from Shutter sequencer contract for slot {keys.Slot} at time {offsetText} slot start...");

        using DecryptedTransactions? decrypted = DecryptSequencedTransactions(sequencedTransactions, keys.Keys);

        if (_logger.IsDebug && decrypted is not null) _logger.Debug($"Decrypted Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, decrypted.Value.Transactions.Select(tx => tx.ToShortString()))}");

        Transaction[] filtered = decrypted is null ? [] : FilterTransactions(decrypted.Value.Transactions, parentHeader);

        ShutterTransactions shutterTransactions = new()
        {
            Transactions = filtered,
            Slot = keys.Slot
        };

        Metrics.ShutterTransactions = (uint)filtered.Length;
        Metrics.ShutterBadTransactions = (uint)(sequencedTransactions.Count - filtered.Length);

        if (_logger.IsDebug && shutterTransactions.Transactions.Length > 0) _logger.Debug($"Filtered Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, shutterTransactions.Transactions.Select(tx => tx.ToShortString()))}");
        return shutterTransactions;
    }

    public void LoadFromReceipts(Block? head, TxReceipt[] receipts, ulong eon)
    {
        lock (_events)
        {
            if (_loadFromReceipts && head is not null)
            {
                IEnumerable<ISequencerContract.TransactionSubmitted> events = _logScanner.ScanReceipts(head.Number, receipts);
                _events.EnqueueEvents(events, eon);
            }
        }
    }

    private Transaction[] FilterTransactions(IEnumerable<Transaction> transactions, BlockHeader parentHeader)
        => transactions.Where(tx => _txFilter.IsAllowed(tx, parentHeader) == TxPool.AcceptTxResult.Accepted).ToArray();

    private DecryptedTransactions? DecryptSequencedTransactions(ArrayPoolList<SequencedTransaction> sequencedTransactions, EnumerableWithCount<(ReadOnlyMemory<byte> IdentityPreimage, ReadOnlyMemory<byte> Key)> decryptionKeys)
    {
        int txCount = sequencedTransactions.Count;
        int keyCount = decryptionKeys.Count;

        if (txCount < keyCount)
        {
            if (_logger.IsError) _logger.Error($"Could not decrypt Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            return null;
        }

        if (txCount > keyCount)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not decrypt all Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            sequencedTransactions.ReduceCount(txCount - keyCount);
            txCount = keyCount;
        }

        using ArrayPoolList<SequencedTransaction> sortedIndexes = sequencedTransactions.ToPooledList();
        sortedIndexes.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        using ArrayPoolList<int> sortedKeyIndexes = new(txCount, txCount);
        int keyIndex = 0;
        foreach (SequencedTransaction index in sortedIndexes)
        {
            sortedKeyIndexes[index.Index] = keyIndex++;
        }

        using var decryptionKeysList = new ArrayPoolList<(ReadOnlyMemory<byte> IdentityPreimage, ReadOnlyMemory<byte> Key)>(decryptionKeys.Count, decryptionKeys);

        ArrayPoolList<Transaction> decryptedTransactions = sequencedTransactions
            .AsParallel()
            .AsOrdered()
            // ReSharper disable AccessToDisposedClosure
            .Select((tx, i) => DecryptSequencedTransaction(tx, decryptionKeysList[sortedKeyIndexes[i]]))
            // ReSharper restore AccessToDisposedClosure
            .OfType<Transaction>()
            .ToPooledList(sequencedTransactions.Count);

        return new()
        {
            Transactions = decryptedTransactions,
            SortedKeyIndexes = sortedKeyIndexes
        };
    }

    private Transaction? DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, (ReadOnlyMemory<byte> IdentityPreimage, ReadOnlyMemory<byte> Key) decryptionKey)
    {
        try
        {
            ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);
            G1 key = new(decryptionKey.Key.Span);
            G1 identity = ShutterCrypto.ComputeIdentity(decryptionKey.IdentityPreimage.Span);

            if (!identity.IsEqual(new(sequencedTransaction.Identity.AsSpan())))
            {
                if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
                return null;
            }

            int len = ShutterCrypto.GetDecryptedDataLength(encryptedMessage);
            Span<byte> encodedTransaction = stackalloc byte[len];
            ShutterCrypto.Decrypt(ref encodedTransaction, encryptedMessage, key);

            if (_logger.IsDebug) _logger.Debug($"Decrypted Shutter transaction, got encoded transaction data: {Convert.ToHexString(encodedTransaction)}");

            return DecodeTransaction(encodedTransaction);
        }
        catch (ShutterCrypto.ShutterCryptoException e)
        {
            if (_logger.IsDebug) _logger.Error($"Could not decode encrypted Shutter transaction", e);
        }
        catch (Bls.Exception e)
        {
            if (_logger.IsDebug) _logger.Error("Could not decrypt Shutter transaction with invalid key", e);
        }
        catch (RlpException e)
        {
            if (_logger.IsDebug) _logger.Error("Could not decode decrypted Shutter transaction", e);
        }
        catch (ArgumentException e)
        {
            if (_logger.IsDebug) _logger.Error("Could not recover Shutter transaction sender address", e);
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsDebug) _logger.Error("Decrypted Shutter transaction had no signature", e);
        }

        return null;
    }

    private Transaction DecodeTransaction(ReadOnlySpan<byte> encoded)
    {
        Transaction tx = TxDecoder.Instance.Decode(encoded, RlpBehaviors.SkipTypedWrapping);
        tx.SenderAddress = ecdsa.RecoverAddress(tx, true);
        return tx;
    }

    private IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer, long headBlockNumber)
    {
        lock (_events)
        {
            if (_loadFromReceipts)
            {
                if (_logger.IsDebug) _logger.Debug($"Found {_events.Count} Shutter events in recent blocks up to {headBlockNumber}, local tx pointer is {_txPointer}.");
            }
            else
            {
                LoadFromScanningLogs(eon, txPointer, headBlockNumber);
                _loadFromReceipts = true;
            }

            IEnumerable<ISequencerContract.TransactionSubmitted> events = _events.DequeueToGasLimit(eon, txPointer);

            int index = 0;
            foreach (ISequencerContract.TransactionSubmitted e in events)
            {
                yield return EventToSequencedTransaction(e, index++, eon);
            }
        }
    }

    private static SequencedTransaction EventToSequencedTransaction(ISequencerContract.TransactionSubmitted e, int index, ulong eon)
    {
        byte[] identityPreimage = new byte[52];
        e.IdentityPrefix.AsSpan().CopyTo(identityPreimage.AsSpan());
        e.Sender.Bytes.CopyTo(identityPreimage.AsSpan()[32..]);

        return new()
        {
            Index = index,
            Eon = eon,
            EncryptedTransaction = e.EncryptedTransaction,
            GasLimit = e.GasLimit,
            Identity = ShutterCrypto.ComputeIdentity(identityPreimage.AsSpan()).Compress(),
            IdentityPreimage = identityPreimage
        };
    }

    private void LoadFromScanningLogs(ulong eon, ulong txPointer, long headBlockNumber)
    {
        _txPointer = txPointer;

        IEnumerable<ISequencerContract.TransactionSubmitted> events = _logScanner.ScanLogs(headBlockNumber, (ISequencerContract.TransactionSubmitted e) => e.Eon == eon && e.TxIndex <= _txPointer);

        int count = 0;
        foreach (ISequencerContract.TransactionSubmitted e in events)
        {
            _events.EnqueueEvent(e, eon);
            count++;
        }

        if (_logger.IsDebug) _logger.Debug($"Found {count} Shutter events from scanning logs up to block {headBlockNumber}, local tx pointer is {_txPointer}.");
    }


    private struct SequencedTransaction
    {
        public int Index;
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public byte[] Identity;
        public byte[] IdentityPreimage;
    }

    private readonly struct DecryptedTransactions : IDisposable
    {
        public ArrayPoolList<Transaction> Transactions { get; init; }
        public ArrayPoolList<int> SortedKeyIndexes { get; init; }

        public void Dispose()
        {
            SortedKeyIndexes.Dispose();
            Transactions.Dispose();
        }
    }
}
