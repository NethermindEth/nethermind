// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NonBlocking;

namespace Nethermind.Optimism.CL;

public class P2PBlockValidator : IP2PBlockValidator
{
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;
    private readonly ConcurrentDictionary<long, long> _numberOfBlocksSeen = new();
    private readonly Address _sequencerP2PAddress;
    private readonly byte[] _chainId;


    public P2PBlockValidator(UInt256 chainId, Address sequencerP2PAddress, ITimestamper timestamper, ILogger logger)
    {
        _logger = logger;
        _timestamper = timestamper;
        _sequencerP2PAddress = sequencerP2PAddress;
        _chainId = chainId.ToBigEndian();
    }

    public ValidityStatus Validate(ExecutionPayloadV3 payload, P2PTopic topic)
    {
        if (!IsTopicValid(topic) || !IsTimestampValid(payload) || !IsBlockHashValid(payload) ||
            !IsBlobGasUsedValid(payload, topic) || !IsExcessBlobGasValid(payload, topic) ||
            !IsParentBeaconBlockRootValid(payload, topic) || IsBlockNumberPerHeightLimitReached(payload))
        {
            return ValidityStatus.Reject;
        }

        return ValidityStatus.Valid;
    }

    public ValidityStatus ValidateSignature(ReadOnlySpan<byte> payloadData, Span<byte> signature)
    {
        return IsSignatureValid(payloadData, signature) ? ValidityStatus.Valid : ValidityStatus.Reject;
    }

    private bool IsTopicValid(P2PTopic topic)
    {
        // Reject everything except V3 for now
        // We assume later that we receive only V3 messages
        if (topic != P2PTopic.BlocksV3)
        {
            if (_logger.IsError) _logger.Error($"Invalid topic: {topic}");
            return false;
        }

        return true;
    }

    private bool IsTimestampValid(ExecutionPayloadV3 payload)
    {
        // [REJECT] if the payload.timestamp is older than 60 seconds in the past (graceful boundary for worst-case propagation and clock skew)
        // [REJECT] if the payload.timestamp is more than 5 seconds into the future
        ulong timestamp = _timestamper.UnixTime.Seconds;
        if (payload.Timestamp < timestamp - 60 || timestamp + 5 < payload.Timestamp)
        {
            if (_logger.IsError) _logger.Error($"Invalid Timestamp: now {timestamp}, payload: {payload.Timestamp}");
            return false;
        }

        return true;
    }

    private bool IsBlockHashValid(ExecutionPayloadV3 payload)
    {
        // [REJECT] if the block_hash in the payload is not valid
        payload.TryGetBlock(out Block? block);
        if (block is null)
        {
            if (_logger.IsError) _logger.Error($"Error creating block");
            return false;
        }

        Hash256 calculatedHash = block.Header.CalculateHash();
        if (payload.BlockHash != calculatedHash)
        {
            if (_logger.IsError) _logger.Error($"Invalid block hash: expected {payload.BlockHash}, got: {calculatedHash}");
            return false;
        }

        return true;
    }

    private bool IsBlobGasUsedValid(ExecutionPayloadV3 payload, P2PTopic topic)
    {
        // [REJECT] if the block is on a topic >= V3 and has a blob gas-used value that is not zero
        if (payload.BlobGasUsed != 0)
        {
            if (_logger.IsError) _logger.Error($"Invalid BlobGasUsed: {payload.BlobGasUsed}");
            return false;
        }

        return true;
    }

    private bool IsExcessBlobGasValid(ExecutionPayloadV3 payload, P2PTopic topic)
    {
        // [REJECT] if the block is on a topic >= V3 and has an excess blob gas value that is not zero
        if (payload.ExcessBlobGas != 0)
        {
            if (_logger.IsError) _logger.Error($"Invalid ExcessBlobGas {payload.ExcessBlobGas}");
            return false;
        }

        return true;
    }

    private bool IsParentBeaconBlockRootValid(ExecutionPayloadV3 payload, P2PTopic topic)
    {
        // [REJECT] if the block is on a topic >= V3 and the parent beacon block root is nil
        if (payload.ParentBeaconBlockRoot is null)
        {
            if (_logger.IsError) _logger.Error($"Invalid BeaconBlockRoot");
            return false;
        }

        return true;
    }

    private bool IsBlockNumberPerHeightLimitReached(ExecutionPayloadV3 payload)
    {
        // [REJECT] if more than 5 different blocks have been seen with the same block height
        long currentCount = _numberOfBlocksSeen.GetOrAdd(payload.BlockNumber, _ => 0);
        _numberOfBlocksSeen[payload.BlockNumber] = currentCount + 1;
        return currentCount > 5;
    }

    private bool IsSignatureValid(ReadOnlySpan<byte> payloadData, Span<byte> signature)
    {
        if (signature[64] > 3) return false;
        // domain(all zeros) + chain id + payload hash
        Span<byte> sequencerSignedData = stackalloc byte[32 + 32 + 32];

        // Array.Copy(_chainId, 0, new byte[10], 32, 32);
        _chainId.CopyTo(sequencerSignedData.Slice(32, 32));
        KeccakHash.ComputeHashBytes(payloadData).CopyTo(sequencerSignedData.Slice(64, 32));
        byte[] signedHash = KeccakHash.ComputeHashBytes(sequencerSignedData);

        Span<byte> publicKey = stackalloc byte[65];
        bool success = SpanSecP256k1.RecoverKeyFromCompact(
            publicKey,
            signedHash,
            signature.Slice(0, 64),
            signature[64],
            false);

        Address? address = success ? PublicKey.ComputeAddress(publicKey.Slice(1, 64)) : null;

        return address == _sequencerP2PAddress;
    }
}
