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
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Consensus.Processing;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    public Dto.DecryptionKeys? DecryptionKeys;
    public ulong? TxPointer; //todo: might not need local txPointer once Shutter updates?
    private bool _validatorsRegistered = false;
    private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly IAuraConfig _auraConfig;
    private readonly ILogger _logger;
    private readonly IEthereumEcdsa _ethereumEcdsa;
    private readonly SequencerContract _sequencerContract;
    private readonly Address ValidatorRegistryContractAddress;
    private readonly IEnumerable<(ulong, byte[])> ValidatorsInfo;
    private readonly UInt256 EncryptedGasLimit;

    public ShutterTxSource(ILogFinder logFinder, IFilterStore filterStore, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ISpecProvider specProvider, ILogManager logManager, IEthereumEcdsa ethereumEcdsa, IEnumerable<(ulong, byte[])> validatorsInfo)
        : base()
    {
        _readOnlyTxProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        _abiEncoder = abiEncoder;
        _auraConfig = auraConfig;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _ethereumEcdsa = ethereumEcdsa;
        _sequencerContract = new(auraConfig.ShutterSequencerContractAddress, logFinder, filterStore);
        ValidatorRegistryContractAddress = new(_auraConfig.ShutterValidatorRegistryContractAddress);
        ValidatorsInfo = validatorsInfo;
        EncryptedGasLimit = _auraConfig.ShutterEncryptedGasLimit;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(parent.StateRoot!);
        ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, ValidatorRegistryContractAddress, _auraConfig, _specProvider, _logger);

        if (!_validatorsRegistered)
        {
            foreach ((ulong validatorIndex, byte[] validatorPubKey) in ValidatorsInfo)
            {
                if (!validatorRegistryContract!.IsRegistered(parent, validatorIndex, validatorPubKey))
                {
                    throw new Exception("Validator " + validatorIndex + " not registered as Shutter validator.");
                }
                _validatorsRegistered = true;
            }
        }

        if (DecryptionKeys is null || DecryptionKeys!.Gnosis.Slot != (ulong)parent.Number || TxPointer is null)
        {
            // todo: store a dictionary?
            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {parent.Number}, cannot include Shutter transactions");
            return [];
        }

        IEnumerable<SequencedTransaction> sequencedTransactions = GetNextTransactions(DecryptionKeys.Eon, TxPointer.Value);
        if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count()} transactions from Shutter mempool...");

        TxPointer = DecryptionKeys.Gnosis.TxPointer;

        // todo: change once keypers are aware of validator contract
        // skip block decryption key, does it do anything?
        // IEnumerable<Transaction> transactions = sequencedTransactions.Zip(DecryptionKeys.Keys).Select(x => DecryptSequencedTransaction(x.Item1, x.Item2));
        IEnumerable<Transaction> transactions = sequencedTransactions.Select(x => DecryptSequencedTransaction(x, new())).OfType<Transaction>();
        if (_logger.IsInfo) _logger.Info($"Decrypted {transactions.Count()} Shutter transactions...");

        transactions.ForEach((tx) =>
        {
            _logger.Info(tx.ToShortString());
        });

        return transactions;
    }

    internal Transaction? DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, Dto.Key decryptionKey)
    {
        ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);

        // todo: remove once Shutter swaps to BLS
        UInt256 sk = 123456789;
        Address senderAddress = new("3834a349678eF446baE07e2AefFC01054184af00");
        Bytes32 identityPrefix = new(Convert.FromHexString("3834a349678eF446baE07e2AefFC01054184af00383438343834383438343834").AsSpan());
        G1 identity = ShutterCrypto.ComputeIdentity(identityPrefix, senderAddress);
        G1 key = identity.dup().mult(sk.ToLittleEndian());

        if (!identity.is_equal(sequencedTransaction.Identity))
        {
            if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: Transaction identity did not match decryption key.");
            return null;
        }

        Transaction transaction;
        try
        {
            byte[] encodedTransaction = ShutterCrypto.Decrypt(encryptedMessage, key);
            transaction = Rlp.Decode<Transaction>(new Rlp(encodedTransaction));
            transaction.SenderAddress = _ethereumEcdsa.RecoverAddress(transaction, true);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug("Could not decrypt Shutter transaction: " + e.Message);
            return null;
        }

        return transaction;
    }

    internal IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, ulong txPointer)
    {
        IEnumerable<ISequencerContract.TransactionSubmitted> events = _sequencerContract.GetEvents();
        _logger.Info("total events: " + events.Count());
        events = events.Where(e => e.Eon == eon);
        _logger.Info("tx pointer: " + txPointer);
        _logger.Info("this eon: " + events.Count());
        events = events.Skip((int)txPointer); //todo: check overflow
        _logger.Info("after skipping: " + events.Count());

        List<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach (ISequencerContract.TransactionSubmitted e in events)
        {
            if (totalGas + e.GasLimit > EncryptedGasLimit)
            {
                _logger.Info("reached gas limit!");
                break;
            }

            SequencedTransaction sequencedTransaction = new()
            {
                Eon = eon,
                EncryptedTransaction = e.EncryptedTransaction,
                GasLimit = e.GasLimit,
                Identity = ShutterCrypto.ComputeIdentity(e.IdentityPrefix, e.Sender)
            };
            txs.Add(sequencedTransaction);

            _logger.Info("added tx");

            totalGas += e.GasLimit;
        }

        return txs;
    }

    internal struct SequencedTransaction
    {
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity;
    }
}
