// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
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
    private readonly ITxSealer _txSealer;
    private UInt64 _nonce;
    private readonly UInt64 _validatorIndex;
    private static readonly string FUNCTION_NAME = "update";
    private static readonly byte VALIDATOR_REGISTRY_MESSAGE_VERSION = 0;

    public ValidatorRegistryContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, ISigner signer, ITxSender txSender, ITxSealer txSealer)
        : base(transactionProcessor, abiEncoder, contractAddress)
    {
        _signer = signer;
        _txSender = txSender;
        _txSealer = txSealer;

        _nonce = _validatorIndex = 0;
        // todo: look back through contract updates to calculate correct nonce
        // what is validator index?
    }

    private void ComputeRegistryMessagePrefix(UInt64 nonce, Span<byte> registryMessagePrefix)
    {
        registryMessagePrefix[0] = VALIDATOR_REGISTRY_MESSAGE_VERSION;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(3), BlockchainIds.Gnosis);
        Span<byte> addressSpan = registryMessagePrefix.Slice(9);
        addressSpan = ContractAddress!.Bytes;
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(31), _validatorIndex);
        BinaryPrimitives.WriteUInt64BigEndian(registryMessagePrefix.Slice(39), nonce);
    }

    private byte[] ComputeDeregistrationMessage(UInt64 nonce)
    {
        Span<byte> registryMessagePrefix = stackalloc byte[46];
        ComputeRegistryMessagePrefix(nonce, registryMessagePrefix);
        return registryMessagePrefix.ToArray();
    }

    private byte[] ComputeRegistrationMessage(UInt64 nonce)
    {
        Span<byte> registryMessagePrefix = stackalloc byte[46];
        ComputeRegistryMessagePrefix(nonce, registryMessagePrefix);
        registryMessagePrefix[45] = 1;
        return registryMessagePrefix.ToArray();
    }

    private async ValueTask<AcceptTxResult?> SendMessage(byte[] message)
    {
        var transaction = GenerateTransaction<GeneratedTransaction>(FUNCTION_NAME, _signer.Address, message);
        await _txSealer.Seal(transaction, TxHandlingOptions.AllowReplacingSignature);
        var (_, res) = await _txSender.SendTransaction(transaction, TxHandlingOptions.PersistentBroadcast);
        return res;
    }

    public async ValueTask<AcceptTxResult?> Deregister(BlockHeader blockHeader)
    {
        byte[] deregistrationMessage = ComputeDeregistrationMessage(_nonce);
        AcceptTxResult? res = await SendMessage(deregistrationMessage);

        if (res == AcceptTxResult.Accepted)
        {
            _nonce++;
        }

        return res;
    }

    public async ValueTask<AcceptTxResult?> Register(BlockHeader blockHeader)
    {
        byte[] registrationMessage = ComputeRegistrationMessage(_nonce);
        AcceptTxResult? res = await SendMessage(registrationMessage);

        if (res == AcceptTxResult.Accepted)
        {
            _nonce++;
        }

        return res;
    }
}
