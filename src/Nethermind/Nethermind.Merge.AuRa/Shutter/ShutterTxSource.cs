// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Abi;
using Nethermind.Crypto;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Consensus.Processing;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using System.IO;
using Google.Protobuf;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    private IEnumerable<Transaction> _loadedTransactions = [];
    private ulong _loadedTransactionsSlot = 0;
    private bool _validatorsRegistered = false;
    private readonly ReadOnlyTxProcessingEnvFactory _readOnlyTxProcessingEnvFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly IAuraConfig _auraConfig;
    private readonly ILogger _logger;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly SequencerContract _sequencerContract;
    private readonly ShutterEon _eon;
    private readonly Address ValidatorRegistryContractAddress;
    private readonly Dictionary<ulong, byte[]> ValidatorsInfo;
    private readonly UInt256 EncryptedGasLimit;
    private readonly ulong InstanceID;

    public ShutterTxSource(ILogFinder logFinder, IFilterStore filterStore, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ISpecProvider specProvider, ILogManager logManager, IEthereumEcdsa ethereumEcdsa, ShutterEon eon, Dictionary<ulong, byte[]> validatorsInfo)
        : base()
    {
        _readOnlyTxProcessingEnvFactory = readOnlyTxProcessingEnvFactory;
        _abiEncoder = abiEncoder;
        _auraConfig = auraConfig;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _ethereumEcdsa = ethereumEcdsa;
        _eon = eon;
        _sequencerContract = new(auraConfig.ShutterSequencerContractAddress, logFinder, filterStore);
        ValidatorRegistryContractAddress = new(_auraConfig.ShutterValidatorRegistryContractAddress);
        ValidatorsInfo = validatorsInfo;
        EncryptedGasLimit = _auraConfig.ShutterEncryptedGasLimit;
        InstanceID = _auraConfig.ShutterInstanceID;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        // assume validator will stay registered
        if (!_validatorsRegistered)
        {
            if (IsRegistered(parent))
            {
                _validatorsRegistered = true;
            }
            else
            {
                return [];
            }
        }

        ulong nextSlot = GetNextSlot();
        if (_loadedTransactionsSlot != nextSlot)
        {
            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {nextSlot}, cannot include Shutter transactions.");
            if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {_loadedTransactionsSlot}");
            return [];
        }

        return _loadedTransactions;
    }

    public void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys)
    {
        if (decryptionKeys.Gnosis.Slot <= _loadedTransactionsSlot)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping Shutter decryption keys from slot {decryptionKeys.Gnosis.Slot}, keys currently stored for slot {_loadedTransactionsSlot}.");
            return;
        }

        ShutterEon.Info? eonInfo = _eon.GetCurrentEonInfo();
        if (eonInfo is null)
        {
            if (_logger.IsDebug) _logger.Debug("Cannot check Shutter decryption keys, eon info was not found.");
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count()} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

        if (CheckDecryptionKeys(decryptionKeys, eonInfo.Value))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");

            IEnumerable<SequencedTransaction> sequencedTransactions = GetNextTransactions(decryptionKeys.Eon, decryptionKeys.Gnosis.TxPointer);
            if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count()} transactions from Shutter mempool...");

            _loadedTransactions = DecryptSequencedTransactions(sequencedTransactions, decryptionKeys);
            _loadedTransactionsSlot = decryptionKeys.Gnosis.Slot;

            if (_logger.IsInfo)
            {
                _loadedTransactions.ForEach((tx) =>
                {
                    _logger.Info(tx.ToShortString());
                });
            }
        }
    }

    internal ulong GetNextSlot()
    {
        // assume Gnosis or Chiado chain
        ulong genesisTimestamp = (_specProvider.ChainId == BlockchainIds.Chiado) ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return (((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - genesisTimestamp) / 5) + 1;
    }

    internal IEnumerable<Transaction> DecryptSequencedTransactions(IEnumerable<SequencedTransaction> sequencedTransactions, Dto.DecryptionKeys decryptionKeys)
    {
        // order by identity preimage to match decryption keys
        IEnumerable<(int, Transaction?)> unorderedTransactions = sequencedTransactions
            .Select((x, index) => x with { Index = index })
            .OrderBy(x => x.IdentityPreimage, new ByteArrayComparer())
            .Zip(decryptionKeys.Keys.Skip(1))
            .Select(x => (x.Item1.Index, DecryptSequencedTransaction(x.Item1, x.Item2)));

        // return decrypted transactions to original order
        IEnumerable<Transaction> transactions = unorderedTransactions.AsQueryable()
            .OrderBy("Item1")
            .Select(x => x.Item2)
            .OfType<Transaction>();

        return transactions;
    }

    internal bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys, in ShutterEon.Info eonInfo)
    {
        if (decryptionKeys.InstanceID != InstanceID)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: instanceID {decryptionKeys.InstanceID} did not match expected value {InstanceID}.");
            return false;
        }

        if (decryptionKeys.Eon != eonInfo.Eon)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: eon {decryptionKeys.Eon} did not match expected value {eonInfo.Eon}.");
            return false;
        }

        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable().Skip(1))
        {
            G1 dk, identity;
            try
            {
                dk = new(key.Key_.ToArray());
                identity = ShutterCrypto.ComputeIdentity(key.Identity.Span);
            }
            catch (ApplicationException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: {e}.");
                return false;
            }

            if (!ShutterCrypto.CheckDecryptionKey(dk, eonInfo.Key, identity))
            {
                if (_logger.IsDebug) _logger.Debug("Invalid decryption keys received on P2P network: decryption key did not match eon key.");
                return false;
            }
        }

        long signerIndicesCount = decryptionKeys.Gnosis.SignerIndices.LongCount();

        if (decryptionKeys.Gnosis.SignerIndices.Distinct().Count() != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: incorrect number of signer indices.");
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count() != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: incorrect number of signatures.");
            return false;
        }

        if (signerIndicesCount != (int)eonInfo.Threshold)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: signer indices did not match threshold.");
            return false;
        }

        List<byte[]> identityPreimages = decryptionKeys.Keys.Select((Dto.Key key) => key.Identity.ToArray()).ToList();

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = eonInfo.Addresses[signerIndex];

            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(InstanceID, eonInfo.Eon, decryptionKeys.Gnosis.Slot, decryptionKeys.Gnosis.TxPointer, identityPreimages, signature.Span, keyperAddress))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: bad signature.");
                return false;
            }
        }

        return true;
    }

    internal bool IsRegistered(BlockHeader parent)
    {
        ITransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessingEnvFactory.Create().Build(parent.StateRoot!).TransactionProcessor;
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, ValidatorRegistryContractAddress, _auraConfig, _specProvider, _logger);
        if (!validatorRegistryContract!.IsRegistered(parent, ValidatorsInfo, out HashSet<ulong> unregistered))
        {
            string unregisteredList = unregistered.Aggregate("", (acc, validatorIndex) => acc == "" ? validatorIndex.ToString() : (acc + ", " + validatorIndex));
            if (_logger.IsError) _logger.Error("Validators not registered to Shutter with the following indices: [" + unregisteredList + "]");
            return false;
        }
        return true;
    }

    internal Transaction? DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, Dto.Key decryptionKey)
    {
        ShutterCrypto.EncryptedMessage encryptedMessage;
        try
        {
            encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);
        }
        catch (ShutterCrypto.ShutterCryptoException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not decode encrypted Shutter transaction: {e}");
            return null;
        }

        G1 key;
        try
        {
            key = new(decryptionKey.Key_.ToArray()); ;
        }
        catch (ApplicationException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not decrypt Shutter transaction with invalid key: {e}");
            return null;
        }

        G1 identity = ShutterCrypto.ComputeIdentity(decryptionKey.Identity.Span);

        if (!identity.is_equal(sequencedTransaction.Identity))
        {
            if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
            return null;
        }

        Transaction transaction;
        try
        {
            byte[] encodedTransaction = ShutterCrypto.Decrypt(encryptedMessage, key);
            transaction = Rlp.Decode<Transaction>(encodedTransaction.AsSpan());
            // todo: test sending transactions with bad signatures to see if secp segfaults
            transaction.SenderAddress = _ethereumEcdsa.RecoverAddress(transaction, true);
        }
        catch (ShutterCrypto.ShutterCryptoException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not decrypt Shutter transaction: {e}");
            return null;
        }
        catch (RlpException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not decode decrypted Shutter transaction: {e}");
            return null;
        }
        catch (ArgumentException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Could not recover Shutter transaction sender address: {e}");
            return null;
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Decrypted Shutter transaction had no signature: {e}");
            return null;
        }

        return transaction;
    }

    internal IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer)
    {
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents(eon);
        if (_logger.IsDebug) _logger.Debug($"Found {events.Count()} events in Shutter sequencer contract for this eon.");

        events = events.Skip(txPointer);

        List<SequencedTransaction> txs = [];
        UInt256 totalGas = 0;

        foreach (ISequencerContract.TransactionSubmitted e in events)
        {
            if (totalGas + e.GasLimit > EncryptedGasLimit)
            {
                if (_logger.IsDebug) _logger.Debug("Shutter gas limit reached.");
                break;
            }

            byte[] identityPreimage = new byte[52];
            e.IdentityPrefix.AsSpan().CopyTo(identityPreimage.AsSpan());
            e.Sender.Bytes.CopyTo(identityPreimage.AsSpan()[32..]);

            SequencedTransaction sequencedTransaction = new()
            {
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

    internal struct SequencedTransaction
    {
        public int Index;
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity;
        public byte[] IdentityPreimage;
    }

    internal class ByteArrayComparer : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null)
            {
                return 0;
            }

            var len = Math.Min(x!.Length, y!.Length);
            for (int i = 0; i < len; i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
