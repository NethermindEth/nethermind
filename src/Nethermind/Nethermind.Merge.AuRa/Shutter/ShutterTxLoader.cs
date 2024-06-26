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
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly SequencerContract _sequencerContract = new(new Address(shutterConfig.SequencerContractAddress), logFinder, logManager);
    private readonly UInt256 _encryptedGasLimit = shutterConfig.EncryptedGasLimit;

    public LoadedTransactions LoadTransactions(ulong eon, ulong txPointer, ulong slot, List<(byte[], byte[])> keys)
    {
        Block head = readOnlyBlockTree.Head!;

        List<SequencedTransaction> sequencedTransactions = GetNextTransactions(eon, txPointer, head.Number);
        if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count} encrypted transactions from Shutter mempool...");

        Transaction[] transactions = DecryptSequencedTransactions(sequencedTransactions, keys);

        if (_logger.IsDebug)
        {
            string msg = "Decrypted Shutter transactions:";
            transactions.ForEach(tx => msg += "\n" + tx.ToShortString());
            _logger.Debug(msg);
        }

        FilterTransactions(ref transactions, head);

        LoadedTransactions loadedTransactions = new()
        {
            Transactions = transactions,
            Slot = slot
        };

        if (_logger.IsDebug)
        {
            string msg = "Filtered Shutter transactions:";
            loadedTransactions.Transactions.ForEach(tx => msg += "\n" + tx.ToShortString());
            _logger.Debug(msg);
        }

        return loadedTransactions;
    }

    private void FilterTransactions(ref Transaction[] transactions, Block? head)
    {
        // question for reviewers: what is correct thing to do here if head is null?
        IReleaseSpec releaseSpec = head is null ? specProvider.GetFinalSpec() : specProvider.GetSpec(head.Number, head.Timestamp);
        TxValidator txValidator = new(specProvider.ChainId);
        transactions = Array.FindAll(transactions, tx => tx.Type != TxType.Blob ^ txValidator.IsWellFormed(tx, releaseSpec));
    }

    internal Transaction[] DecryptSequencedTransactions(List<SequencedTransaction> sequencedTransactions, List<(byte[], byte[])> decryptionKeys)
    {
        using ArrayPoolList<SequencedTransaction> sortedIndexes = sequencedTransactions.ToPooledList();
        sortedIndexes.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        return sequencedTransactions
            .AsParallel()
            .AsOrdered()
            .Select((tx, i) => DecryptSequencedTransaction(tx, decryptionKeys[sortedIndexes[i].Index + 1]))
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

            // todo: remove after using for testing
            if (_logger.IsDebug) _logger.Debug($"Decrypted raw Shutter transaction: {Convert.ToHexString(encodedTransaction)}");

            Transaction transaction = Rlp.Decode<Transaction>(encodedTransaction.AsSpan());
            // todo: test sending transactions with bad signatures to see if secp segfaults
            transaction.SenderAddress = ethereumEcdsa.RecoverAddress(transaction, true);

            if (_logger.IsDebug) _logger.Debug($"Decoded Shutter transaction: {transaction.ToShortString()}");

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

    private List<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer, long headBlockNumber)
    {
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents(eon, txPointer, headBlockNumber);
        if (_logger.IsDebug) _logger.Debug($"Found {events.Count()} events in Shutter sequencer contract.");

        List<SequencedTransaction> txs = [];
        UInt256 totalGas = 0;
        int index = 0;

        foreach (ISequencerContract.TransactionSubmitted e in events)
        {
            if (totalGas + e.GasLimit > _encryptedGasLimit)
            {
                if (_logger.IsDebug) _logger.Debug("Shutter gas limit reached.");
                break;
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
            txs.Add(sequencedTransaction);
            totalGas += e.GasLimit;
        }

        return txs;
    }

    public struct LoadedTransactions
    {
        public Transaction[] Transactions { get; init; }
        public ulong Slot { get; init; }
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
