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
using System.Collections.Generic;
using Nethermind.Core.Extensions;
using Update = (byte[] Message, byte[] Signature);

namespace Nethermind.Shutter.Contracts;

public class ValidatorRegistryContract(
    ITransactionProcessor transactionProcessor,
    IAbiEncoder abiEncoder,
    Address contractAddress,
    ILogManager logManager,
    ulong chainId,
    ulong messageVersion)
    : CallableContract(transactionProcessor, abiEncoder, contractAddress), IValidatorRegistryContract
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public UInt256 GetNumUpdates(BlockHeader header) => (UInt256)Call(header, nameof(GetNumUpdates), Address.Zero, [])[0];

    public Update GetUpdate(BlockHeader header, in UInt256 i)
        => (Update)Call(header, nameof(GetUpdate), Address.Zero, [i])[0];

    public bool IsRegistered(BlockHeader header, in Dictionary<ulong, byte[]> validatorsInfo, out HashSet<ulong> unregistered)
    {
        Dictionary<ulong, ulong?> nonces = [];
        unregistered = [];
        foreach (KeyValuePair<ulong, byte[]> validatorInfo in validatorsInfo)
        {
            nonces.Add(validatorInfo.Key, null);
            unregistered.Add(validatorInfo.Key);
        }

        uint updates = (uint)GetNumUpdates(header);
        for (uint i = 0; i < updates; i++)
        {
            Update update = GetUpdate(header, updates - i - 1);
            Message msg = new(update.Message.AsSpan()[..46]);

            // skip untracked validators
            if (!validatorsInfo.ContainsKey(msg.ValidatorIndex))
            {
                continue;
            }

            if (msg.Version != messageVersion)
            {
                if (_logger.IsInfo) _logger.Info($"Registration message has wrong version ({msg.Version}) should be {messageVersion}");
                continue;
            }

            if (msg.ChainId != chainId)
            {
                if (_logger.IsInfo) _logger.Info($"Registration message has incorrect chain ID ({msg.ChainId}) should be {chainId}");
                continue;
            }

            if (!msg.ContractAddress.SequenceEqual(ContractAddress!.Bytes))
            {
                if (_logger.IsInfo) _logger.Info($"Registration message contains an invalid contract address ({msg.ContractAddress.ToHexString()}) should be {ContractAddress}");
                continue;
            }

            if (nonces[msg.ValidatorIndex].HasValue && msg.Nonce <= nonces[msg.ValidatorIndex])
            {
                if (_logger.IsInfo) _logger.Info($"Registration message has incorrect nonce ({msg.Nonce}) should be {nonces[msg.ValidatorIndex]}");
                continue;
            }

            if (!ShutterCrypto.CheckValidatorRegistrySignature(validatorsInfo[msg.ValidatorIndex], update.Signature, update.Message))
            {
                if (_logger.IsInfo) _logger.Info("Registration message has invalid signature.");
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

    private readonly ref struct Message
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
