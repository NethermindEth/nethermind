// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa.Shutter;

public class ValidatorRegistryContract : CallableContract, IValidatorRegistryContract
{
    private readonly ISigner _signer;
    private readonly ITxSender _txSender;
    private static readonly string FUNCTION_NAME = "update";
    private static readonly byte VALIDATOR_REGISTRY_MESSAGE_VERSION = 0;

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, ISigner signer, ITxSender txSender)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _signer = signer;
        _txSender = txSender;
    }

    // def compute_registration_message(validator_index: uint64, nonce: uint64):
    //     return compute_registry_message_prefix(validator_index, nonce) + b"\x01"

    // def compute_deregistration_message(validator_index: uint64, nonce: uint64):
    //     return compute_registry_message_prefix(validator_index, nonce) + b"\x00"

    // def compute_registry_message_prefix(validator_index: uint64, nonce: uint64):
    //     return VALIDATOR_REGISTRY_MESSAGE_VERSION + CHAIN_ID.to_bytes(8, "big") + VALIDATOR_REGISTRY_ADDRESS + validator_index.to_bytes(8, "big") + nonce.to_bytes(8, "big")

    private byte[] ComputeRegistryMessagePrefix(UInt64 validatorIndex, UInt64 nonce, bool isRegistration)
    {
        Span<byte> registryMessagePrefix = stackalloc byte[46];

        registryMessagePrefix[0] = VALIDATOR_REGISTRY_MESSAGE_VERSION;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(3), BlockchainIds.Gnosis);
        Span<byte> addressSpan = registryMessagePrefix.Slice(9);
        addressSpan = ContractAddress!.Bytes;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(31), validatorIndex);
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(39), nonce);

        if (isRegistration)
        {
            registryMessagePrefix[45] = 1;
        }

        return registryMessagePrefix.ToArray();
    }

    public void Deregister(BlockHeader blockHeader, byte[] message, byte[] signature)
    {
        UInt64 nonce = 0; // load nonce from disk
        UInt64 validatorIndex = 0;
        byte[] deregistrationMessage = ComputeRegistryMessagePrefix(nonce, validatorIndex, false);

        var transaction = GenerateTransaction<GeneratedTransaction>(FUNCTION_NAME, _signer.Address, deregistrationMessage);
        // sign transaction?

        _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
    }

    public void Register(BlockHeader blockHeader, byte[] message, byte[] signature)
    {
        throw new NotImplementedException();
    }
}
