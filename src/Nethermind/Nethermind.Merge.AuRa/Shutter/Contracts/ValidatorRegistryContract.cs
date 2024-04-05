// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Logging;
using static Nethermind.Merge.AuRa.Shutter.Contracts.IValidatorRegistryContract;

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

    public bool IsRegistered(BlockHeader blockHeader, ulong validatorIndex, byte[] validatorPubKey)
    {
        UInt256 updates = GetNumUpdates(blockHeader);
        for (UInt256 i = 0; i < updates; i++)
        {
            Update update = GetUpdate(blockHeader, updates - i - 1);
            Message msg = new(update.Message.AsSpan()[..46]);
            if (msg.ValidatorIndex != validatorIndex)
            {
                continue;
            }

            BlsSigner.PublicKey pk = new()
            {
                Bytes = validatorPubKey
            };
            BlsSigner.Signature sig = new()
            {
                Bytes = update.Signature
            };

            // todo: check if nonce is correct

            if (!msg.IsRegistration)
            {
                return false;
            }
            else if (msg.Version != _auraConfig.ShutterValidatorRegistryMessageVersion)
            {
                if (_logger.IsWarn) _logger.Warn("Registration message has wrong version (" + msg.Version + ") should be " + _auraConfig.ShutterValidatorRegistryMessageVersion);
                continue;
            }
            else if (msg.ChainId != _specProvider.ChainId)
            {
                if (_logger.IsWarn) _logger.Warn("Registration message has incorrect chain ID (" + msg.ChainId + ") should be " + _specProvider.ChainId);
                continue;
            }
            else if (msg.ContractAddress != ContractAddress)
            {
                if (_logger.IsWarn) _logger.Warn("Registration message contains an invalid contract address (" + msg.ContractAddress + ") should be " + ContractAddress);
                continue;
            }
            // todo: check signature
            // else if (!BlsSigner.Verify(pk, sig, update.Message))
            // {
            //     if (_logger.IsWarn) _logger.Warn("Registration message has invalid signature.");
            //     continue;
            // }

            return true;
        }
        return false;
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
                throw new Exception("Encoded validator registry contract message was malformed.");
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
