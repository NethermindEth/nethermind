// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;
using static Nethermind.Merge.AuRa.Shutter.Contracts.IValidatorRegistryContract;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter.Contracts;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    private readonly ISigner _signer;
    private readonly ITxSender _txSender;
    private readonly ITxSealer _txSealer;
    private const string update = "update";
    private const string getNumUpdates = "getNumUpdates";
    private const string getUpdate = "getUpdate";

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, ISigner signer, ITxSender txSender, ITxSealer txSealer)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _signer = signer;
        _txSender = txSender;
        _txSealer = txSealer;
    }

    public async Task RegisterValidators(BlockHeader blockHeader, IEnumerable<ValidatorInfo> validatorsInfo)
    {
        foreach (ValidatorInfo validatorInfo in validatorsInfo)
        {
            if (!IsRegistered(blockHeader, validatorInfo.Address))
            {
                AcceptTxResult? res = await SendUpdate(validatorInfo.Message, validatorInfo.Signature);

                if (res != AcceptTxResult.Accepted)
                {
                    throw new Exception("Failed to register as Shutter validator for validator index " + validatorInfo.ValidatorIndex);
                }
            }
        }
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

    public async ValueTask<AcceptTxResult?> SendUpdate(byte[] message, byte[] signature)
    {
        Transaction transaction = GenerateTransaction<GeneratedTransaction>(update, _signer.Address, new[] { message, signature });
        await _txSealer.Seal(transaction, TxHandlingOptions.AllowReplacingSignature);
        (Hash256 _, AcceptTxResult? res) = await _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
        return res;
    }

    public struct ValidatorInfo
    {
        public string ValidatorIndex;
        public Address Address;
        public byte[] Message;
        public byte[] Signature;
    }

    internal bool IsRegistered(BlockHeader blockHeader, Address validatorAddress)
    {
        UInt256 updates = GetNumUpdates(blockHeader);
        for (UInt256 i = updates - 1; i >= 0; i -= 1)
        {
            Message msg = GetUpdateMessage(blockHeader, i);
            if (msg.Sender == validatorAddress && msg.IsRegistration)
            {
                return true;
            }
        }
        return false;
    }

    internal Message GetUpdateMessage(BlockHeader blockHeader, UInt256 i)
    {
        Update update = GetUpdate(blockHeader, i);
        return new Message(update.Message.AsSpan()[..46]);
    }

    internal class Message
    {
        public readonly byte Version;
        public readonly ulong ChainId;
        public readonly Address Sender;
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
            Sender = new Address(encodedMessage[9..29].ToArray());
            ValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[29..]);
            Nonce = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[37..]);
            IsRegistration = encodedMessage[45] == 1;
        }
    }
}
