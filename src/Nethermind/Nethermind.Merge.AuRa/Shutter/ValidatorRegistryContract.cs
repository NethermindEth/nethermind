// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa.Shutter;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    private readonly ISigner _signer;
    private readonly ITxSender _txSender;
    private readonly ITxSealer _txSealer;
    private readonly IBlockchainBridge _blockchainBridge;
    private UInt64 _nonce;
    private readonly UInt64 _validatorIndex;
    private static readonly string UPDATE = "update";
    private static readonly string GET_NUM_UPDATES = "getNumUpdates";
    private static readonly string GET_UPDATE = "getUpdate";
    private static readonly byte VALIDATOR_REGISTRY_MESSAGE_VERSION = 0;

    private class Message
    {
        public readonly byte Version;
        public readonly UInt64 ChainId;
        public readonly Address Sender;
        public readonly UInt64 ValidatorIndex;
        public readonly UInt64 Nonce;

        public Message(Address sender, UInt64 validatorIndex, UInt64 nonce)
        {
            Version = VALIDATOR_REGISTRY_MESSAGE_VERSION;
            ChainId = BlockchainIds.Gnosis;
            Sender = sender;
            ValidatorIndex = validatorIndex;
            Nonce = nonce;
        }

        public Message(Span<byte> encodedMessage)
        {
            if (encodedMessage.Length != 46)
            {
                throw new Exception("Encoded validator registry contract message was malformed.");
            }

            Version = encodedMessage[0];
            ChainId = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage.Slice(3));
            Sender = new Address(encodedMessage.Slice(9, 20).ToArray());
            ValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage.Slice(31));
            Nonce = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage.Slice(39));
        }

        private void ComputeRegistryMessagePrefix(Span<byte> registryMessagePrefix)
        {
            registryMessagePrefix[0] = Version;
            BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(3), ChainId);
            Span<byte> addressSpan = registryMessagePrefix.Slice(9);
            addressSpan = Sender.Bytes;
            BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(31), ValidatorIndex);
            BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(39), Nonce);
        }

        public byte[] ComputeDeregistrationMessage()
        {
            Span<byte> registryMessagePrefix = stackalloc byte[46];
            ComputeRegistryMessagePrefix(registryMessagePrefix);
            return registryMessagePrefix.ToArray();
        }

        public byte[] ComputeRegistrationMessage()
        {
            Span<byte> registryMessagePrefix = stackalloc byte[46];
            ComputeRegistryMessagePrefix(registryMessagePrefix);
            registryMessagePrefix[45] = 1;
            return registryMessagePrefix.ToArray();
        }
    }

    private UInt256 GetNumUpdates(BlockHeader blockHeader)
    {
        Transaction transaction = GenerateTransaction<SystemTransaction>(GET_NUM_UPDATES, _signer.Address, Array.Empty<object>());
        BlockchainBridge.CallOutput res = _blockchainBridge.Call(blockHeader, transaction, new System.Threading.CancellationToken());
        return new UInt256(res.OutputData, true);
    }

    private Message GetUpdateMessage(BlockHeader blockHeader, UInt256 i)
    {
        Transaction transaction = GenerateTransaction<SystemTransaction>(GET_UPDATE, _signer.Address, new[] {i});
        BlockchainBridge.CallOutput res = _blockchainBridge.Call(blockHeader, transaction, new System.Threading.CancellationToken());
        Span<byte> encodedMessage = res.OutputData;
        // ignore signature for now, maybe should verify?
        return new Message(encodedMessage.Slice(0, 46));
    }

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, ISigner signer, ITxSender txSender, ITxSealer txSealer, IBlockchainBridge blockchainBridge, BlockHeader blockHeader)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _signer = signer;
        _txSender = txSender;
        _txSealer = txSealer;
        _blockchainBridge = blockchainBridge;
        _validatorIndex = 0; // what is this?

        // set nonce based on last nonce observed from this address
        _nonce = 0;
        UInt256 update = GetNumUpdates(blockHeader);
        for (UInt256 i = update - 1; i >= 0; i -= 1)
        {
            Message m = GetUpdateMessage(blockHeader, i);
            if (m.Sender == ContractAddress!)
            {
                _nonce = m.Nonce + 1;
                break;
            }
        }
    }

    private byte[] Sign(byte[] message)
    {
        // todo: this uses secp256k1, we want BLS
        return _signer.Sign(Keccak.Compute(message)).Bytes;
    }

    private async ValueTask<AcceptTxResult?> Update(byte[] message, byte[] signature)
    {
        Transaction transaction = GenerateTransaction<GeneratedTransaction>(UPDATE, _signer.Address, new[] {message, signature});
        await _txSealer.Seal(transaction, TxHandlingOptions.AllowReplacingSignature);
        (Hash256 _, AcceptTxResult? res) = await _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
        return res;
    }

    public async ValueTask<AcceptTxResult?> Deregister(BlockHeader blockHeader)
    {
        byte[] deregistrationMessage = new Message(ContractAddress!, _validatorIndex, _nonce).ComputeDeregistrationMessage();
        AcceptTxResult? res = await Update(deregistrationMessage, Sign(deregistrationMessage));

        if (res == AcceptTxResult.Accepted)
        {
            _nonce++;
        }

        return res;
    }

    public async ValueTask<AcceptTxResult?> Register(BlockHeader blockHeader)
    {
        byte[] registrationMessage = new Message(ContractAddress!, _validatorIndex, _nonce).ComputeRegistrationMessage();
        AcceptTxResult? res = await Update(registrationMessage, Sign(registrationMessage));

        if (res == AcceptTxResult.Accepted)
        {
            _nonce++;
        }

        return res;
    }
}
