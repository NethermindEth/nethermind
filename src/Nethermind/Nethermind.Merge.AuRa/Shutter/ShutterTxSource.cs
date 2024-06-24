// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Abi;
using Nethermind.Crypto;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
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
using Nethermind.Consensus.Validators;
using Nethermind.Blockchain;

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    private LoadedTransactions _loadedTransactions;
    private bool _validatorsRegistered;
    private readonly ReadOnlyTxProcessingEnvFactory _envFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly IReadOnlyBlockTree _readOnlyBlockTree;
    private readonly SequencerContract _sequencerContract;
    private readonly ShutterEon _eon;
    private readonly Address _validatorRegistryContractAddress;
    private readonly Dictionary<ulong, byte[]> _validatorsInfo;
    private readonly UInt256 _encryptedGasLimit;
    private readonly ulong _validatorRegistryMessageVersion;
    private readonly ulong _instanceId;

    public ShutterTxSource(ILogFinder logFinder,
        IFilterStore filterStore,
        ReadOnlyTxProcessingEnvFactory envFactory,
        IAbiEncoder abiEncoder,
        IShutterConfig shutterConfig,
        ISpecProvider specProvider,
        IEthereumEcdsa ethereumEcdsa,
        IReadOnlyBlockTree readOnlyBlockTree,
        ShutterEon eon,
        Dictionary<ulong, byte[]> validatorsInfo,
        ILogManager logManager)
    {
        _envFactory = envFactory;
        _abiEncoder = abiEncoder;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _ethereumEcdsa = ethereumEcdsa;
        _readOnlyBlockTree = readOnlyBlockTree;
        _eon = eon;
        _sequencerContract = new(shutterConfig.SequencerContractAddress, logFinder, filterStore);
        _validatorRegistryContractAddress = new(shutterConfig.ValidatorRegistryContractAddress);
        _validatorsInfo = validatorsInfo;
        _encryptedGasLimit = shutterConfig.EncryptedGasLimit;
        _validatorRegistryMessageVersion = shutterConfig.ValidatorRegistryMessageVersion;
        _instanceId = shutterConfig.InstanceID;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        // assume validator will stay registered
        if (!_validatorsRegistered)
        {
            if (!IsRegistered(parent))
            {
                return [];
            }

            _validatorsRegistered = true;
        }

        ulong nextSlot = GetNextSlot();
        // atomic fetch
        LoadedTransactions loadedTransactions = _loadedTransactions;
        if (loadedTransactions.Slot == nextSlot) return loadedTransactions.Transactions;
        if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {nextSlot}, cannot include Shutter transactions.");
        if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {loadedTransactions.Slot}");
        return [];
    }

    public void OnDecryptionKeysReceived(Dto.DecryptionKeys decryptionKeys)
    {
        // atomic fetch
        LoadedTransactions loadedTransactions = _loadedTransactions;

        if (decryptionKeys.Gnosis.Slot <= loadedTransactions.Slot)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping Shutter decryption keys from slot {decryptionKeys.Gnosis.Slot}, keys currently stored for slot {loadedTransactions.Slot}.");
            return;
        }

        ShutterEon.Info? eonInfo = _eon.GetCurrentEonInfo();
        if (eonInfo is null)
        {
            if (_logger.IsDebug) _logger.Debug("Cannot check Shutter decryption keys, eon info was not found.");
            return;
        }

        if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

        if (CheckDecryptionKeys(decryptionKeys, eonInfo.Value))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");

            List<SequencedTransaction> sequencedTransactions = GetNextTransactions(decryptionKeys.Eon, decryptionKeys.Gnosis.TxPointer);
            if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count} transactions from Shutter mempool...");

            Transaction[] transactions = DecryptSequencedTransactions(sequencedTransactions, decryptionKeys);

            Block? head = _readOnlyBlockTree.Head;
            IReleaseSpec releaseSpec = head is null ? _specProvider.GetFinalSpec() : _specProvider.GetSpec(head.Number, head.Timestamp);
            TxValidator txValidator = new(_specProvider.ChainId);
            transactions = Array.FindAll(transactions, tx => tx.Type != TxType.Blob ^ txValidator.IsWellFormed(tx, releaseSpec));

            // atomic update
            _loadedTransactions = new()
            {
                Transactions = transactions,
                Slot = decryptionKeys.Gnosis.Slot
            };

            if (_logger.IsDebug)
            {
                string msg = "Decrypted Shutter transactions:";
                _loadedTransactions.Transactions.ForEach(tx => msg += "\n" + tx.ToShortString());
                _logger.Debug(msg);
            }
        }
    }

    private ulong GetNextSlot()
    {
        // assume Gnosis or Chiado chain
        ulong genesisTimestamp = (_specProvider.ChainId == BlockchainIds.Chiado) ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return (((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - genesisTimestamp) / 5) + 1;
    }

    private Transaction[] DecryptSequencedTransactions(List<SequencedTransaction> sequencedTransactions, Dto.DecryptionKeys decryptionKeys)
    {
        using ArrayPoolList<SequencedTransaction> sortedIndexes = sequencedTransactions.ToPooledList();
        sortedIndexes.Sort((a, b) => Bytes.BytesComparer.Compare(a.IdentityPreimage, b.IdentityPreimage));

        return sequencedTransactions
            .AsParallel()
            .Select((tx, i) => DecryptSequencedTransaction(tx, decryptionKeys.Keys[sortedIndexes[i].Index + 1]))
            .OfType<Transaction>()
            .ToArray();
    }

    private bool CheckDecryptionKeys(in Dto.DecryptionKeys decryptionKeys, in ShutterEon.Info eonInfo)
    {
        if (decryptionKeys.InstanceID != _instanceId)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: instanceID {decryptionKeys.InstanceID} did not match expected value {_instanceId}.");
            return false;
        }

        if (decryptionKeys.Eon != eonInfo.Eon)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: eon {decryptionKeys.Eon} did not match expected value {eonInfo.Eon}.");
            return false;
        }

        // skip placeholder transaction
        foreach (Dto.Key key in decryptionKeys.Keys.AsEnumerable().Skip(1))
        {
            G1 dk, identity;
            try
            {
                dk = new(key.Key_.ToArray());
                identity = ShutterCrypto.ComputeIdentity(key.Identity.Span);
            }
            catch (Bls.Exception e)
            {
                if (_logger.IsDebug) _logger.Error("Invalid decryption keys received on P2P network.", e);
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
            if (_logger.IsDebug) _logger.Debug("Invalid decryption keys received on P2P network: incorrect number of signer indices.");
            return false;
        }

        if (decryptionKeys.Gnosis.Signatures.Count != signerIndicesCount)
        {
            if (_logger.IsDebug) _logger.Debug("Invalid decryption keys received on P2P network: incorrect number of signatures.");
            return false;
        }

        if (signerIndicesCount != (int)eonInfo.Threshold)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: signer indices did not match threshold.");
            return false;
        }

        List<byte[]> identityPreimages = decryptionKeys.Keys.Select(key => key.Identity.ToArray()).ToList();

        foreach ((ulong signerIndex, ByteString signature) in decryptionKeys.Gnosis.SignerIndices.Zip(decryptionKeys.Gnosis.Signatures))
        {
            Address keyperAddress = eonInfo.Addresses[signerIndex];

            if (!ShutterCrypto.CheckSlotDecryptionIdentitiesSignature(_instanceId, eonInfo.Eon, decryptionKeys.Gnosis.Slot, decryptionKeys.Gnosis.TxPointer, identityPreimages, signature.Span, keyperAddress))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid decryption keys received on P2P network: bad signature.");
                return false;
            }
        }

        return true;
    }

    private bool IsRegistered(BlockHeader parent)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _envFactory.Create().Build(parent.StateRoot!);
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, _validatorRegistryContractAddress, _logger, _specProvider.ChainId, _validatorRegistryMessageVersion);
        if (!validatorRegistryContract!.IsRegistered(parent, _validatorsInfo, out HashSet<ulong> unregistered))
        {
            if (_logger.IsError) _logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            return false;
        }
        return true;
    }

    private Transaction? DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, Dto.Key decryptionKey)
    {
        try
        {
            ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);
            G1 key = new(decryptionKey.Key_.ToArray());
            G1 identity = ShutterCrypto.ComputeIdentity(decryptionKey.Identity.Span);

            if (!identity.is_equal(sequencedTransaction.Identity))
            {
                if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
                return null;
            }

            byte[] encodedTransaction = ShutterCrypto.Decrypt(encryptedMessage, key);

            // todo: remove after using for testing
            if (_logger.IsDebug) _logger.Debug($"Decrypted Shutter message: {Convert.ToHexString(encodedTransaction)}");

            Transaction transaction = Rlp.Decode<Transaction>(encodedTransaction.AsSpan());
            // todo: test sending transactions with bad signatures to see if secp segfaults
            transaction.SenderAddress = _ethereumEcdsa.RecoverAddress(transaction, true);
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

    private List<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer)
    {
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents(eon, txPointer);
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

    private struct LoadedTransactions
    {

        public Transaction[] Transactions { get; init; }
        public ulong Slot { get; init; }
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
