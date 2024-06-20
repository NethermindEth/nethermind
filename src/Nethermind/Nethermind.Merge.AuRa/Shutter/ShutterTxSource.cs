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

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    private IEnumerable<Transaction> _loadedTransactions = [];
    private ulong _loadedTransactionsSlot;
    private bool _validatorsRegistered;
    private readonly ReadOnlyTxProcessingEnvFactory _envFactory;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly IAuraConfig _auraConfig;
    private readonly ILogger _logger;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly SequencerContract _sequencerContract;
    private readonly ShutterEon _eon;
    private readonly Address _validatorRegistryContractAddress;
    private readonly Dictionary<ulong, byte[]> _validatorsInfo;
    private readonly UInt256 _encryptedGasLimit;
    private readonly ulong _instanceId;

    public ShutterTxSource(ILogFinder logFinder,
        IFilterStore filterStore,
        ReadOnlyTxProcessingEnvFactory envFactory,
        IAbiEncoder abiEncoder,
        IAuraConfig auraConfig,
        ISpecProvider specProvider,
        IEthereumEcdsa ethereumEcdsa,
        ShutterEon eon,
        Dictionary<ulong, byte[]> validatorsInfo,
        ILogManager logManager)
    {
        _envFactory = envFactory;
        _abiEncoder = abiEncoder;
        _auraConfig = auraConfig;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _ethereumEcdsa = ethereumEcdsa;
        _eon = eon;
        _sequencerContract = new(auraConfig.ShutterSequencerContractAddress, logFinder, filterStore);
        _validatorRegistryContractAddress = new(_auraConfig.ShutterValidatorRegistryContractAddress);
        _validatorsInfo = validatorsInfo;
        _encryptedGasLimit = _auraConfig.ShutterEncryptedGasLimit;
        _instanceId = _auraConfig.ShutterInstanceID;
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
        if (_loadedTransactionsSlot == nextSlot) return _loadedTransactions;
        if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {nextSlot}, cannot include Shutter transactions.");
        if (_logger.IsDebug) _logger.Debug($"Current Shutter decryption keys stored for slot {_loadedTransactionsSlot}");
        return [];
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

        if (_logger.IsDebug) _logger.Debug($"Checking Shutter decryption keys instanceID: {decryptionKeys.InstanceID} eon: {decryptionKeys.Eon} #keys: {decryptionKeys.Keys.Count} #sig: {decryptionKeys.Gnosis.Signatures.Count()} #txpointer: {decryptionKeys.Gnosis.TxPointer} #slot: {decryptionKeys.Gnosis.Slot}");

        if (CheckDecryptionKeys(decryptionKeys, eonInfo.Value))
        {
            if (_logger.IsInfo) _logger.Info($"Validated Shutter decryption key for slot {decryptionKeys.Gnosis.Slot}");

            List<SequencedTransaction> sequencedTransactions = GetNextTransactions(decryptionKeys.Eon, decryptionKeys.Gnosis.TxPointer);
            if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count} transactions from Shutter mempool...");

            _loadedTransactions = DecryptSequencedTransactions(sequencedTransactions, decryptionKeys);
            _loadedTransactionsSlot = decryptionKeys.Gnosis.Slot;

            if (_logger.IsInfo)
            {
                _loadedTransactions.ForEach(tx => _logger.Info(tx.ToShortString()));
            }
        }
    }

    private ulong GetNextSlot()
    {
        // assume Gnosis or Chiado chain
        ulong genesisTimestamp = (_specProvider.ChainId == BlockchainIds.Chiado) ? ChiadoSpecProvider.BeaconChainGenesisTimestamp : GnosisSpecProvider.BeaconChainGenesisTimestamp;
        return (((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - genesisTimestamp) / 5) + 1;
    }

    private IEnumerable<Transaction> DecryptSequencedTransactions(IEnumerable<SequencedTransaction> sequencedTransactions, Dto.DecryptionKeys decryptionKeys)
    {
        // order by identity preimage to match decryption keys
        IEnumerable<(int, Transaction?)> unorderedTransactions = sequencedTransactions
            .Select((x, index) => x with { Index = index })
            .OrderBy(x => x.IdentityPreimage, Bytes.Comparer)
            .Zip(decryptionKeys.Keys.Skip(1))
            .Select(x => (x.Item1.Index, DecryptSequencedTransaction(x.Item1, x.Item2)));

        // return decrypted transactions to original order
        IEnumerable<Transaction> transactions = unorderedTransactions
            .OrderBy(x => x.Item1)
            .Select(x => x.Item2)
            .OfType<Transaction>();

        return transactions;
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
        ITransactionProcessor readOnlyTransactionProcessor = _envFactory.Create().Build(parent.StateRoot!).TransactionProcessor;
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, _validatorRegistryContractAddress, _auraConfig, _specProvider, _logger);
        if (!validatorRegistryContract!.IsRegistered(parent, _validatorsInfo, out HashSet<ulong> unregistered))
        {
            string unregisteredList = unregistered.Aggregate("", (acc, validatorIndex) => acc == "" ? validatorIndex.ToString() : acc + ", " + validatorIndex);
            if (_logger.IsError) _logger.Error("Validators not registered to Shutter with the following indices: [" + unregisteredList + "]");
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
            Transaction transaction = Rlp.Decode<Transaction>(encodedTransaction.AsSpan());
            // todo: test sending transactions with bad signatures to see if secp segfaults
            transaction.SenderAddress = _ethereumEcdsa.RecoverAddress(transaction, true);
            return transaction;
        }
        catch (ShutterCrypto.ShutterCryptoException e)
        {
            if (_logger.IsDebug) _logger.Error($"Could not decode encrypted Shutter transaction", e);
            return null;
        }
        catch (Bls.Exception e)
        {
            if (_logger.IsDebug) _logger.Error("Could not decrypt Shutter transaction with invalid key", e);
            return null;
        }
        catch (RlpException e)
        {
            if (_logger.IsDebug) _logger.Error("Could not decode decrypted Shutter transaction", e);
            return null;
        }
        catch (ArgumentException e)
        {
            if (_logger.IsDebug) _logger.Error("Could not recover Shutter transaction sender address", e);
            return null;
        }
        catch (InvalidDataException e)
        {
            if (_logger.IsDebug) _logger.Error("Decrypted Shutter transaction had no signature", e);
            return null;
        }
    }

    private List<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer)
    {
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents(eon);
        if (_logger.IsDebug) _logger.Debug($"Found {events.Count()} events in Shutter sequencer contract for this eon.");

        events = events.Skip(txPointer);

        List<SequencedTransaction> txs = [];
        UInt256 totalGas = 0;

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
