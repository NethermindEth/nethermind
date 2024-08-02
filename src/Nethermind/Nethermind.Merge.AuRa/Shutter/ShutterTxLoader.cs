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
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using System.IO;
using Nethermind.Consensus.Validators;
using Nethermind.Blockchain;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxLoader(
    ILogFinder logFinder,
    IShutterConfig shutterConfig,
    ISpecProvider specProvider,
    IEthereumEcdsa ethereumEcdsa,
    IReadOnlyBlockTree readOnlyBlockTree,
    ILogManager logManager)
{
    private readonly TxValidator _txValidator = new(specProvider.ChainId);
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly SequencerContract _sequencerContract = new(new Address(shutterConfig.SequencerContractAddress!), logFinder, logManager);
    private readonly UInt256 _encryptedGasLimit = shutterConfig.EncryptedGasLimit;
    private readonly ulong _genesisTimestampMs = ShutterHelpers.GetGenesisTimestampMs(specProvider);

    public ShutterTransactions LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        Block? head = readOnlyBlockTree.Head;

        List<SequencedTransaction> sequencedTransactions = GetNextTransactions(eon, txPointer, head?.Number ?? 0).ToList();
        long offset = ShutterHelpers.GetCurrentOffsetMs(slot, _genesisTimestampMs);
        string offsetText = offset < 0 ? $"{-offset}ms before" : $"{offset}ms after";
        if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count} encrypted transactions from Shutter mempool for slot {slot} at time {offsetText} slot start...");

        Transaction[] transactions = DecryptSequencedTransactions(sequencedTransactions, keys);

        if (_logger.IsDebug && transactions.Length > 0) _logger.Debug($"Decrypted Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, transactions.Select(tx => tx.ToShortString()))}");

        IReleaseSpec releaseSpec = head is null ? specProvider.GetFinalSpec() : specProvider.GetSpec(head.Number, head.Timestamp);
        Transaction[] filtered = FilterTransactions(transactions, releaseSpec).ToArray();

        ShutterTransactions shutterTransactions = new()
        {
            Transactions = filtered,
            Slot = slot
        };

        if (_logger.IsDebug && shutterTransactions.Transactions.Length > 0) _logger.Debug($"Filtered Shutter transactions:{Environment.NewLine}{string.Join(Environment.NewLine, shutterTransactions.Transactions.Select(tx => tx.ToShortString()))}");
        return shutterTransactions;
    }

    internal IEnumerable<Transaction> FilterTransactions(IEnumerable<Transaction> transactions, IReleaseSpec releaseSpec)
    {
        foreach (Transaction tx in transactions)
        {
            bool wellFormed = _txValidator.IsWellFormed(tx, releaseSpec, out string? error);

            if (_logger.IsDebug)
            {
                if (!wellFormed) _logger.Debug($"Decrypted Shutter transactions was not well-formed{(error is null ? "." : ": " + error)}");
                if (tx.Type == TxType.Blob) _logger.Debug("Decrypted Shutter transaction was blob, cannot include.");
            }

            if (wellFormed && tx.Type != TxType.Blob)
            {
                yield return tx;
            }
        }
    }

    internal Transaction[] DecryptSequencedTransactions(List<SequencedTransaction> sequencedTransactions, List<(byte[], byte[])> decryptionKeys)
    {
        int txCount = sequencedTransactions.Count;
        int keyCount = decryptionKeys.Count - 1;

        if (txCount < keyCount)
        {
            if (_logger.IsError) _logger.Error($"Could not decrypt Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            return [];
        }

        if (txCount > keyCount)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not decrypt all Shutter transactions: found {txCount} transactions but received {keyCount} keys (excluding placeholder).");
            sequencedTransactions = sequencedTransactions[..keyCount];
            txCount = keyCount;
        }

        using ArrayPoolList<SequencedTransaction> sortedIndexes = sequencedTransactions.ToPooledList();
        sortedIndexes.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        using ArrayPoolList<int> sortedKeyIndexes = new(txCount, txCount);
        int keyIndex = 1;
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
                if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
                return null;
            }

            byte[] encodedTransaction = ShutterCrypto.Decrypt(encryptedMessage, key);

            if (_logger.IsDebug) _logger.Debug($"Decrypted Shutter transaction, got encoded transaction data: {Convert.ToHexString(encodedTransaction)}");

            // N.B. does not work with encodedTransaction.AsSpan()
            Transaction transaction = Rlp.Decode<Transaction>(encodedTransaction);
            transaction.SenderAddress = ethereumEcdsa.RecoverAddress(transaction, true);

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
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents(eon, txPointer, headBlockNumber);
        if (_logger.IsDebug) _logger.Debug($"Found {events.Count()} events in Shutter sequencer contract.");

        UInt256 totalGas = 0;
        int index = 0;

        foreach (ISequencerContract.TransactionSubmitted e in events)
        {
            if (totalGas + e.GasLimit > _encryptedGasLimit)
            {
                if (_logger.IsDebug) _logger.Debug("Shutter gas limit reached.");
                yield break;
            }

            byte[] identityPreimage = new byte[52];
            e.IdentityPrefix.AsSpan().CopyTo(identityPreimage.AsSpan());
            e.Sender.Bytes.CopyTo(identityPreimage.AsSpan()[32..]);

            SequencedTransaction sequencedTransaction = new()
            {
                Index = index++,
                Eon = eon,
                EncryptedTransaction = e.EncryptedTransaction,
                GasLimit = e.GasLimit,
                Identity = ShutterCrypto.ComputeIdentity(identityPreimage),
                IdentityPreimage = identityPreimage
            };

            totalGas += e.GasLimit;
            yield return sequencedTransaction;
        }
    }

    internal struct SequencedTransaction
    {
        public int Index;
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity;
        public byte[] IdentityPreimage;
    }
}
