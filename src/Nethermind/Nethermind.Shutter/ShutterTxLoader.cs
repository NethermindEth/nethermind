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
    private readonly ShutterTxFilter _txFilter = new(specProvider, logManager);
    private readonly ILogger _logger = logManager.GetClassLogger();

    public ShutterTransactions LoadTransactions(Block? head, BlockHeader parentHeader, IShutterKeyValidator.ValidatedKeyArgs keys)
    {
        var sequencedTransactions = GetNextTransactions(keys.Eon, keys.TxPointer, head?.Number ?? 0).ToList();

        long offset = time.GetCurrentOffsetMs(keys.Slot);
        Metrics.KeysReceivedTimeOffset = offset;
        string offsetText = offset < 0 ? $"{-offset}ms before" : $"{offset}ms after";
        _logger.Info($"Got {sequencedTransactions.Count} encrypted transactions from Shutter sequencer contract for slot {keys.Slot} at time {offsetText} slot start...");

        Transaction[] transactions = DecryptSequencedTransactions(sequencedTransactions, keys.Keys);

        if (_logger.IsDebug && transactions.Length > 0) _logger.Debug($"Decrypted Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, transactions.Select(tx => tx.ToShortString()))}");

        Transaction[] filtered = FilterTransactions(transactions, parentHeader).ToArray();

        ShutterTransactions shutterTransactions = new()
        {
            Transactions = filtered,
            Slot = keys.Slot
        };

        Metrics.Transactions = (uint)filtered.Length;
        Metrics.BadTransactions = (uint)(sequencedTransactions.Count - filtered.Length);

        if (_logger.IsDebug && shutterTransactions.Transactions.Length > 0) _logger.Debug($"Filtered Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, shutterTransactions.Transactions.Select(tx => tx.ToShortString()))}");
        return shutterTransactions;
    }

    public void LoadFromReceipts(Block head, TxReceipt[] receipts, ulong eon)
    {
        lock (_events)
        {
            if (_loadFromReceipts && head is not null)
            {
                var events = _logScanner.ScanReceipts(head.Number, receipts).ToList();
                _events.EnqueueEvents(events, eon);
            }
        }
    }

    private IEnumerable<Transaction> FilterTransactions(IEnumerable<Transaction> transactions, BlockHeader parentHeader)
    {
        foreach (Transaction tx in transactions)
        {
            if (_txFilter.IsAllowed(tx, parentHeader) == TxPool.AcceptTxResult.Accepted)
            {
                yield return tx;
            }
        }
    }

    private Transaction[] DecryptSequencedTransactions(List<SequencedTransaction> sequencedTransactions, List<(byte[], byte[])> decryptionKeys)
    {
        int txCount = sequencedTransactions.Count;
        int keyCount = decryptionKeys.Count;

        if (txCount < keyCount)
        {
            _logger.Error($"Could not decrypt Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            return [];
        }

        if (txCount > keyCount)
        {
            _logger.Warn($"Could not decrypt all Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            sequencedTransactions = sequencedTransactions[..keyCount];
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

        return sequencedTransactions
            .AsParallel()
            .AsOrdered()
            .Select((tx, i) => DecryptSequencedTransaction(tx, decryptionKeys[sortedKeyIndexes[i]]))
            .OfType<Transaction>()
            .ToArray();
    }

    private Transaction? DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, (byte[] IdentityPreimage, byte[] Key) decryptionKey)
    {
        try
        {
            ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);
            G1 key = new(decryptionKey.Key);
            G1 identity = ShutterCrypto.ComputeIdentity(decryptionKey.IdentityPreimage.AsSpan());

            if (!identity.is_equal(sequencedTransaction.Identity))
            {
                _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
                return null;
            }

            byte[] encodedTransaction = ShutterCrypto.Decrypt(encryptedMessage, key);

            _logger.Debug($"Decrypted Shutter transaction, got encoded transaction data: {Convert.ToHexString(encodedTransaction)}");

            // N.B. does not work with encodedTransaction.AsSpan()
            Transaction transaction = Rlp.Decode<Transaction>(encodedTransaction);
            transaction.SenderAddress = ecdsa.RecoverAddress(transaction, true);

            return transaction;
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

    private IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer, long headBlockNumber)
    {
        lock (_events)
        {
            if (_loadFromReceipts)
            {
                _logger.Debug($"Found {_events.Count} Shutter events in recent blocks up to {headBlockNumber}, local tx pointer is {_txPointer}.");
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
            Identity = ShutterCrypto.ComputeIdentity(identityPreimage),
            IdentityPreimage = identityPreimage
        };
    }

    private void LoadFromScanningLogs(ulong eon, ulong txPointer, long headBlockNumber)
    {
        _txPointer = txPointer;

        var events = _logScanner.ScanLogs(headBlockNumber, (ISequencerContract.TransactionSubmitted e) => e.Eon == eon && e.TxIndex <= _txPointer).ToList();
        _events.EnqueueEvents(events, eon);

        _logger.Debug($"Found {_events.Count} Shutter events from scanning logs up to block {headBlockNumber}, local tx pointer is {_txPointer}.");
    }


    private struct SequencedTransaction
    {
        public int Index;
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity;
        public byte[] IdentityPreimage;
    }
}
