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
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using Nethermind.Consensus.Processing;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

using G1 = Bls.P1;

public class ShutterTxSource : ITxSource
{
    public Dto.DecryptionKeys? DecryptionKeys;
    private readonly ILogFinder? _logFinder;
    private readonly LogFilter? _logFilter;
    private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
    private readonly IAbiEncoder _abiEncoder;
    private readonly ISpecProvider _specProvider;
    private readonly IAuraConfig _auraConfig;
    private readonly ILogger _logger;
    private readonly Address _validatorRegistryContractAddress;
    private IEnumerable<(ulong, byte[])> _validatorsInfo;
    private bool _validatorsRegistered = false;
    private static readonly UInt256 EncryptedGasLimit = 21000 * 10; // todo: change
    internal static readonly AbiSignature TransactionSubmmitedSig = new AbiSignature(
        "TransactionSubmitted",
        [
            AbiType.UInt64, // eon
            AbiType.Bytes32, // identity prefix
            AbiType.Address, // sender
            AbiType.DynamicBytes, // encrypted transaction
            AbiType.UInt256 // gas limit
        ]
    );

    public ShutterTxSource(ILogFinder logFinder, IFilterStore filterStore, ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory, IAbiEncoder abiEncoder, IAuraConfig auraConfig, ISpecProvider specProvider, ILogManager logManager, IEnumerable<(ulong, byte[])> validatorsInfo)
        : base()
    {
        IEnumerable<object> topics = new List<object>() { TransactionSubmmitedSig.Hash };
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, auraConfig.ShutterSequencerContractAddress, topics);
        _readOnlyTxProcessorSource = readOnlyTxProcessingEnvFactory.Create();
        _abiEncoder = abiEncoder;
        _auraConfig = auraConfig;
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _validatorRegistryContractAddress = new(_auraConfig.ShutterValidatorRegistryContractAddress);
        _validatorsInfo = validatorsInfo;
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        IReadOnlyTransactionProcessor readOnlyTransactionProcessor = _readOnlyTxProcessorSource.Build(parent.StateRoot!);
        Contracts.ValidatorRegistryContract validatorRegistryContract = new(readOnlyTransactionProcessor, _abiEncoder, _validatorRegistryContractAddress, _auraConfig, _specProvider, _logger);

        if (!_validatorsRegistered)
        {
            foreach ((ulong validatorIndex, byte[] validatorPubKey) in _validatorsInfo)
            {
                if (!validatorRegistryContract!.IsRegistered(parent, validatorIndex, validatorPubKey))
                {
                    throw new Exception("Validator " + validatorIndex + " not registered as Shutter validator.");
                }
                _validatorsRegistered = true;
            }
        }

        // todo: cache? check changes in header?
        if (DecryptionKeys is null || DecryptionKeys!.Gnosis.Slot != (ulong)parent.Number)
        {
            // todo: store a dictionary?
            if (_logger.IsWarn) _logger.Warn($"Decryption keys not received for slot {parent.Number}, cannot include Shutter transactions");
            return [];
        }

        IEnumerable<SequencedTransaction> sequencedTransactions = GetNextTransactions(DecryptionKeys.Eon, (int)DecryptionKeys.Gnosis.TxPointer);
        if (_logger.IsInfo) _logger.Info($"Got {sequencedTransactions.Count()} transactions from Shutter mempool...");

        IEnumerable<Transaction> transactions = sequencedTransactions.Zip(DecryptionKeys.Keys).Select(x => DecryptSequencedTransaction(x.Item1, x.Item2));
        if (_logger.IsInfo) _logger.Info("Decrypted Shutter transactions...");

        return transactions;
    }

    internal IEnumerable<TransactionSubmittedEvent> GetEvents()
    {
        IEnumerable<IFilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        _logger.Info("total logs: " + logs.Count());
        var tmp = logs.Select(log => new TransactionSubmittedEvent(AbiEncoder.Instance.Decode(AbiEncodingStyle.None, TransactionSubmmitedSig, log.Data)));
        _logger.Info("tx submitted logs: " + tmp.Count());
        return tmp;
    }

    internal Transaction DecryptSequencedTransaction(SequencedTransaction sequencedTransaction, Dto.Key decryptionKey)
    {
        ShutterCrypto.EncryptedMessage encryptedMessage = ShutterCrypto.DecodeEncryptedMessage(sequencedTransaction.EncryptedTransaction);

        if (!new G1(decryptionKey.Identity.ToArray()).is_equal(sequencedTransaction.Identity))
        {
            throw new Exception("Transaction identity did not match decryption key.");
        }

        byte[] transaction = ShutterCrypto.Decrypt(encryptedMessage, new G1(decryptionKey.Key_.ToArray()));
        return Rlp.Decode<Transaction>(new Rlp(transaction), RlpBehaviors.AllowUnsigned);
    }

    internal IEnumerable<SequencedTransaction> GetNextTransactions(ulong eon, int txPointer)
    {
        IEnumerable<TransactionSubmittedEvent> events = GetEvents();
        // todo: use txpointer once correct
        events = events.Where(e => e.Eon == eon);
        // events = events.Where(e => e.Eon == eon).Skip(txPointer);
        _logger.Info("tx pointer: " + txPointer);
        _logger.Info("this eon: " + events.Count());
        events = events.Skip(10);
        _logger.Info("after skipping: " + events.Count());

        List<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach (TransactionSubmittedEvent e in events)
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

    internal class TransactionSubmittedEvent
    {
        public ulong Eon;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;

        public TransactionSubmittedEvent(object[] decodedEvent)
        {
            Eon = (ulong)decodedEvent[0];
            IdentityPrefix = new Bytes32((byte[])decodedEvent[1]);
            Sender = (Address)decodedEvent[2];
            EncryptedTransaction = (byte[])decodedEvent[3];
            GasLimit = (UInt256)decodedEvent[4];
        }
    }
}
