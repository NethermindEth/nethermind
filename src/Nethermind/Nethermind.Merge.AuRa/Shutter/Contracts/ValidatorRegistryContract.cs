// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using static Nethermind.Merge.AuRa.Shutter.Contracts.IValidatorRegistryContract;
using System.Collections.Generic;
using Nethermind.Core.Extensions;

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class ValidatorRegistryContract(
    ITransactionProcessor transactionProcessor,
    IAbiEncoder abiEncoder,
    Address contractAddress,
    ILogger logger,
    ulong chainId,
    ulong messageVersion)
    : CallableContract(transactionProcessor, abiEncoder, contractAddress), IValidatorRegistryContract
{
    private const string getNumUpdates = "getNumUpdates";
    private const string getUpdate = "getUpdate";

    public UInt256 GetNumUpdates(BlockHeader blockHeader) => (UInt256)Call(blockHeader, getNumUpdates, Address.Zero, [])[0];

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
        foreach (KeyValuePair<ulong, byte[]> validatorInfo in validatorsInfo)
        {
            nonces.Add(validatorInfo.Key, null);
            unregistered.Add(validatorInfo.Key);
        }

        uint updates = (uint)GetNumUpdates(blockHeader);
        for (uint i = 0; i < updates; i++)
        {
            Update update = GetUpdate(blockHeader, updates - i - 1);
            Message msg = new(update.Message.AsSpan()[..46]);

            // skip untracked validators
            if (!validatorsInfo.ContainsKey(msg.ValidatorIndex))
            {
                continue;
            }

            if (msg.Version != messageVersion)
            {
                if (logger.IsDebug) logger.Debug($"Registration message has wrong version ({msg.Version}) should be {messageVersion}");
                continue;
            }

            if (msg.ChainId != chainId)
            {
                if (logger.IsDebug) logger.Debug($"Registration message has incorrect chain ID ({msg.ChainId}) should be {chainId}");
                continue;
            }

            if (!msg.ContractAddress.SequenceEqual(ContractAddress!.Bytes))
            {
                if (logger.IsDebug) logger.Debug($"Registration message contains an invalid contract address ({msg.ContractAddress.ToHexString()}) should be {ContractAddress}");
                continue;
            }

            if (nonces[msg.ValidatorIndex].HasValue && msg.Nonce <= nonces[msg.ValidatorIndex])
            {
                if (logger.IsDebug) logger.Debug($"Registration message has incorrect nonce ({msg.Nonce}) should be {nonces[msg.ValidatorIndex]}");
                continue;
            }

            if (!ShutterCrypto.CheckValidatorRegistrySignature(validatorsInfo[msg.ValidatorIndex], update.Signature, update.Message))
            {
                if (logger.IsDebug) logger.Debug("Registration message has invalid signature.");
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

    private ref struct Message
    {
        public readonly byte Version;
        public readonly ulong ChainId;
        public readonly ReadOnlySpan<byte> ContractAddress;
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
            ContractAddress = encodedMessage[9..29];
            ValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[29..]);
            Nonce = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[37..]);
            IsRegistration = encodedMessage[45] == 1;
        }
    }
}
