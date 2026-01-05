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
using Nethermind.Crypto;
using Nethermind.Shutter.Config;
using System.Linq;
using System.Threading;
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

    public bool IsRegistered(in BlockHeader header, in ShutterValidatorsInfo validatorsInfo, out HashSet<ulong> unregistered, CancellationToken cancellationToken)
        => IsRegistered(GetUpdates(header), validatorsInfo, out unregistered, cancellationToken);

    internal bool IsRegistered(IEnumerable<(uint, Update)> updates, in ShutterValidatorsInfo validatorsInfo, out HashSet<ulong> unregistered, CancellationToken cancellationToken)
    {
        Dictionary<ulong, ulong?> nonces = [];
        unregistered = [];
        foreach (ulong index in validatorsInfo.ValidatorIndices)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            nonces.Add(index, null);
            unregistered.Add(index);
        }

        foreach ((uint i, Update update) in updates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (!IsUpdateValid(update, validatorsInfo, out string err))
            {
                if (_logger.IsDebug) _logger.Debug($"Update {i} was invalid: {err}");
                continue;
            }

            Message msg = new(update.Message.AsSpan());
            UpdateRegistrations(msg, nonces, unregistered);
        }

        return unregistered.Count == 0;
    }

    private IEnumerable<(uint, Update)> GetUpdates(BlockHeader header)
    {
        uint updates = (uint)GetNumUpdates(header);
        for (uint i = 0; i < updates; i++)
        {
            yield return (i, GetUpdate(header, updates - i - 1));
        }
    }

    private void UpdateRegistrations(Message msg, Dictionary<ulong, ulong?> nonces, HashSet<ulong> unregistered)
    {
        ulong endValidatorIndex = msg.StartValidatorIndex + msg.Count;
        for (ulong v = msg.StartValidatorIndex; v < endValidatorIndex; v++)
        {
            if (nonces[v].HasValue && msg.Nonce <= nonces[v])
            {
                if (_logger.IsDebug) _logger.Debug($"Registration message for validator index {v} has incorrect nonce ({msg.Nonce}) should be {nonces[v] + 1}");
                continue;
            }

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

    private bool IsUpdateValid(in Update update, in ShutterValidatorsInfo validatorsInfo, out string err)
    {
        if (update.Message.Length != Message.Sz || update.Signature.Length != BlsSigner.Signature.Sz)
        {
            err = "Registration message was wrong length.";
            return false;
        }

        Message msg = new(update.Message.AsSpan());

        ulong startValidatorIndex = msg.StartValidatorIndex;
        ulong endValidatorIndex = msg.StartValidatorIndex + msg.Count;

        // skip validator indices that are definitely not in validators info file
        if (!validatorsInfo.MayContainIndexInRange(startValidatorIndex, endValidatorIndex))
        {
            err = "";
            return false;
        }

        if (msg.Count == 0)
        {
            err = "Registration message has zero registration keys.";
            return false;
        }

        if (msg.Version > messageVersion)
        {
            err = $"Registration message has wrong version ({msg.Version}) should be {messageVersion}.";
            return false;
        }

        if (msg.ChainId != chainId)
        {
            err = $"Registration message has incorrect chain ID ({msg.ChainId}) should be {chainId}.";
            return false;
        }

        if (!msg.ContractAddress.SequenceEqual(ContractAddress!.Bytes))
        {
            err = $"Registration message contains an invalid contract address ({msg.ContractAddress.ToHexString()}) should be {ContractAddress}.";
            return false;
        }

        BlsSigner.AggregatedPublicKey pk = new(stackalloc long[Bls.P1.Sz]);
        for (ulong v = startValidatorIndex; v < endValidatorIndex; v++)
        {
            if (!validatorsInfo.ContainsIndex(v))
            {
                err = $"Registration message contains a validator index that was not found in validator info file ({v}).";
                return false;
            }
            pk.Aggregate(validatorsInfo.GetPubKey(v));
        }

        if (!ShutterCrypto.CheckValidatorRegistrySignatures(pk, update.Signature, update.Message))
        {
            err = "Registration message has invalid signature.";
            return false;
        }

        err = "";
        return true;
    }

    internal readonly ref struct Message
    {
        public const int Sz = 46;
        public readonly byte Version { get; init; }
        public readonly ulong ChainId { get; init; }
        public readonly ReadOnlySpan<byte> ContractAddress { get; init; }
        public readonly ulong StartValidatorIndex { get; init; }
        public readonly uint Count { get; init; }
        public readonly uint Nonce { get; init; }
        public readonly bool IsRegistration { get; init; }

        public Message(Span<byte> encodedMessage)
        {
            if (encodedMessage.Length != Sz)
            {
                throw new ArgumentException("Validator registry contract message was wrong length.");
            }

            byte version = encodedMessage[0];
            Version = version;
            ChainId = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[1..]);
            ContractAddress = encodedMessage[9..29];
            StartValidatorIndex = BinaryPrimitives.ReadUInt64BigEndian(encodedMessage[29..37]);
            Count = version == 0 ? 1 : BinaryPrimitives.ReadUInt32BigEndian(encodedMessage[37..41]);
            Nonce = BinaryPrimitives.ReadUInt32BigEndian(encodedMessage[41..45]);
            IsRegistration = encodedMessage[45] == 1;
        }

        internal byte[] Encode()
        {
            byte[] encoded = new byte[Sz];
            encoded[0] = Version;
            BinaryPrimitives.WriteUInt64BigEndian(encoded.AsSpan()[1..], ChainId);
            ContractAddress.CopyTo(encoded.AsSpan()[9..]);
            BinaryPrimitives.WriteUInt64BigEndian(encoded.AsSpan()[29..], StartValidatorIndex);
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan()[37..], Count);
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan()[41..], Nonce);
            encoded[45] = IsRegistration ? (byte)1 : (byte)0;
            return encoded;
        }
    }
}
