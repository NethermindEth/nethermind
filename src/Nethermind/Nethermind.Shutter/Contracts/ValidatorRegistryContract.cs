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
using Nethermind.Crypto;
using Nethermind.Shutter.Config;
using System.Linq;

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

    public bool IsRegistered(in BlockHeader header, in ShutterValidatorsInfo validatorsInfo, out HashSet<ulong> unregistered)
    {
        Dictionary<ulong, ulong?> nonces = [];
        unregistered = [];
        foreach (ulong index in validatorsInfo.ValidatorIndices)
        {
            nonces.Add(index, null);
            unregistered.Add(index);
        }

        uint updates = (uint)GetNumUpdates(header);
        for (uint i = 0; i < updates; i++)
        {
            Update update = GetUpdate(header, updates - i - 1);

            if (update.Message.Length != Message.Sz || update.Signature.Length != BlsSigner.Signature.Sz)
            {
                if (_logger.IsDebug) _logger.Debug("Registration message was wrong length.");
                continue;
            }

            Message msg = new(update.Message.AsSpan());
            ulong startValidatorIndex = msg.StartValidatorIndex;
            ulong endValidatorIndex = msg.StartValidatorIndex + msg.Count;

            if (msg.Count == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"Registration message has zero registration keys");
                continue;
            }

            if (msg.Version != messageVersion)
            {
                if (_logger.IsDebug) _logger.Debug($"Registration message has wrong version ({msg.Version}) should be {messageVersion}");
                continue;
            }

            if (msg.ChainId != chainId)
            {
                if (_logger.IsDebug) _logger.Debug($"Registration message has incorrect chain ID ({msg.ChainId}) should be {chainId}");
                continue;
            }

            if (!msg.ContractAddress.SequenceEqual(ContractAddress!.Bytes))
            {
                if (_logger.IsDebug) _logger.Debug($"Registration message contains an invalid contract address ({msg.ContractAddress.ToHexString()}) should be {ContractAddress}");
                continue;
            }

            // only check validators in info file
            bool untrackedValidator = false;
            for (ulong v = msg.StartValidatorIndex; v < endValidatorIndex; v++)
            {
                if (!validatorsInfo.IsIndexRegistered(v))
                {
                    untrackedValidator = true;
                    break;
                }
            }
            if (untrackedValidator)
            {
                continue;
            }

            // if (!ShutterCrypto.CheckValidatorRegistrySignature(validatorsInfo.GetPubKey(msg.ValidatorIndex), update.Signature, update.Message))

            // if (nonces[msg.ValidatorIndex].HasValue && msg.Nonce <= nonces[msg.ValidatorIndex])
            // {
            //     if (_logger.IsDebug) _logger.Debug($"Registration message has incorrect nonce ({msg.Nonce}) should be {nonces[msg.ValidatorIndex]}");
            //     continue;
            // }

            // todo: fix overflows
            uint sz = BlsSigner.PkCompressedSz * msg.Count;
            // using ArrayPoolList<byte> buf = new(sz, sz);
            // Span<byte> publicKeys = new byte[sz];

            // for (ulong v = startValidatorIndex; v < endValidatorIndex; v++)
            // {
            //     validatorsInfo.GetPubKey(v).CopyTo(publicKeys[((int)v * BlsSigner.PkCompressedSz)..]);
            // }

            // validatorsInfo.GetPubKey(msg.ValidatorIndex)
            BlsSigner.AggregatedPublicKey aggregatedPublicKey = new();
            if (!ShutterCrypto.CheckValidatorRegistrySignatures(aggregatedPublicKey, update.Signature, update.Message))
            {
                if (_logger.IsDebug) _logger.Debug("Registration message has invalid signature.");
                continue;
            }

            for (ulong v = startValidatorIndex; v < endValidatorIndex; v++)
            {
                if (nonces[v].HasValue && msg.Nonce <= nonces[v])
                {
                    if (_logger.IsDebug) _logger.Debug($"Registration message for validator index {v} has incorrect nonce ({msg.Nonce}) should be {nonces[v] + 1}");
                    continue;
                }

                // message is valid
                nonces[v] = msg.Nonce;

                if (msg.IsRegistration)
                {
                    unregistered.Remove(v);
                }
                else
                {
                    unregistered.Add(v);
                }
            }
        }

        return unregistered.Count == 0;
    }

    private readonly ref struct Message
    {
        public const int Sz = 46;
        public readonly byte Version;
        public readonly ulong ChainId;
        public readonly ReadOnlySpan<byte> ContractAddress;
        public readonly ulong StartValidatorIndex;
        public readonly uint Count;
        public readonly uint Nonce;
        public readonly bool IsRegistration;

        public Message(Span<byte> encodedMessage)
        {
            if (encodedMessage.Length != Sz)
            {
                throw new ArgumentException("Validator registry contract message was wrong length.");
            }

            Version = encodedMessage[0];
            ChainId = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[1..]);
            ContractAddress = encodedMessage[9..29];
            StartValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[29..37]);
            Count = BinaryPrimitives.ReadUInt32BigEndian(encodedMessage[37..41]);
            Nonce = BinaryPrimitives.ReadUInt32BigEndian(encodedMessage[41..45]);
            IsRegistration = encodedMessage[45] == 1;
        }
    }
}
