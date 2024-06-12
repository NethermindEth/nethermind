// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using static Nethermind.Merge.AuRa.Shutter.Contracts.IValidatorRegistryContract;
using System.Collections.Generic;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    private const string getNumUpdates = "getNumUpdates";
    private const string getUpdate = "getUpdate";
    private readonly IAuraConfig _auraConfig;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, IAuraConfig auraConfig, ISpecProvider specProvider, ILogger logger)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _auraConfig = auraConfig;
        _specProvider = specProvider;
        _logger = logger;
    }

    public UInt256 GetNumUpdates(BlockHeader blockHeader)
    {
        object[] res = Call(blockHeader, getNumUpdates, Address.Zero, []);
        return (UInt256)res[0];
    }

    public Update GetUpdate(BlockHeader blockHeader, in UInt256 i)
    {
        var res = (ValueTuple<byte[], byte[]>)Call(blockHeader, getUpdate, Address.Zero, [i])[0];
        Update update = new()
        {
            Message = res.Item1,
            Signature = res.Item2
        };
        return update;
    }

    public bool IsRegistered(BlockHeader blockHeader, in Dictionary<ulong, byte[]> validatorsInfo, out HashSet<ulong> unregistered)
    {
        Dictionary<ulong, ulong?> nonces = [];
        unregistered = [];
        foreach (var validatorInfo in validatorsInfo)
        {
            nonces.Add(validatorInfo.Key, null);
            unregistered.Add(validatorInfo.Key);
        }

        UInt256 updates = GetNumUpdates(blockHeader);
        for (UInt256 i = 0; i < updates; i++)
        {
            Update update = GetUpdate(blockHeader, updates - i - 1);
            Message msg = new(update.Message.AsSpan()[..46]);

            // skip untracked validators
            if (!validatorsInfo.ContainsKey(msg.ValidatorIndex))
            {
                continue;
            }

            if (msg.Version != _auraConfig.ShutterValidatorRegistryMessageVersion)
            {
                if (_logger.IsDebug) _logger.Debug("Registration message has wrong version (" + msg.Version + ") should be " + _auraConfig.ShutterValidatorRegistryMessageVersion);
                continue;
            }
            else if (msg.ChainId != _specProvider.ChainId)
            {
                if (_logger.IsDebug) _logger.Debug("Registration message has incorrect chain ID (" + msg.ChainId + ") should be " + _specProvider.ChainId);
                continue;
            }
            else if (msg.ContractAddress != ContractAddress)
            {
                if (_logger.IsDebug) _logger.Debug("Registration message contains an invalid contract address (" + msg.ContractAddress + ") should be " + ContractAddress);
                continue;
            }
            else if (nonces[msg.ValidatorIndex].HasValue && msg.Nonce <= nonces[msg.ValidatorIndex])
            {
                if (_logger.IsDebug) _logger.Debug("Registration message has incorrect nonce (" + msg.Nonce + ") should be " + nonces[msg.ValidatorIndex]);
                continue;
            }
            else if (!ShutterCrypto.CheckValidatorRegistrySignature(validatorsInfo[msg.ValidatorIndex], update.Signature, update.Message))
            {
                if (_logger.IsDebug) _logger.Debug("Registration message has invalid signature.");
                continue;
            }

            // message is valid
            nonces[msg.ValidatorIndex] = msg.Nonce;

            if (msg.IsRegistration)
            {
                unregistered.Remove(msg.ValidatorIndex);
            }
            else
            {
                unregistered.Add(msg.ValidatorIndex);
            }
        }

        return unregistered.Count == 0;
    }

    internal class Message
    {
        public readonly byte Version;
        public readonly ulong ChainId;
        public readonly Address ContractAddress;
        public readonly ulong ValidatorIndex;
        public readonly ulong Nonce;
        public readonly bool IsRegistration;

        public Message(Span<byte> encodedMessage)
        {
            if (encodedMessage.Length != 46)
            {
                throw new ArgumentException("Validator registry contract message was wrong length.");
            }

            Version = encodedMessage[0];
            ChainId = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[1..]);
            ContractAddress = new Address(encodedMessage[9..29].ToArray());
            ValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[29..]);
            Nonce = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[37..]);
            IsRegistration = encodedMessage[45] == 1;
        }
    }
}
