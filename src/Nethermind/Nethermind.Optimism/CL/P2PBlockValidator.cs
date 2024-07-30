// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
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
    private readonly byte[] _chainId;


    public P2PBlockValidator(UInt256 chainId, ITimestamper timestamper, ILogger logger)
    {
        _logger = logger;
        _timestamper = timestamper;
        _chainId = chainId.ToBigEndian();
    }

    public ValidityStatus Validate(ExecutionPayloadV3 payload, byte[] payloadData, byte[] signature, P2PTopic topic)
    {
        if (!IsTopicValid(topic) || !IsTimestampValid(payload) || !IsBlockHashValid(payload) ||
            !IsBlobGasUsedValid(payload, topic) || !IsExcessBlobGasValid(payload, topic) ||
            !IsParentBeaconBlockRootValid(payload, topic) || IsBlockNumberPerHightLimitReached(payload) ||
            !IsSignatureValid(payloadData, signature))
        {
            return ValidityStatus.Reject;
        }

        if (AlreadySeen(payload))
        {
            return ValidityStatus.Ignore;
        }

        return ValidityStatus.Valid;
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

    private bool IsBlockNumberPerHightLimitReached(ExecutionPayloadV3 payload)
    {
        // [REJECT] if more than 5 different blocks have been seen with the same block height
        // TODO: make thread safe
        // return _numberOfBlocksSeen[payload.BlockNumber] <= 5;
        return false;
    }

    private bool IsSignatureValid(byte[] payloadData, byte[] signature)
    {
        // domain(all zeros) + chain id + payload hash
        byte[] SequencerSignedData = new byte[32 + 32 + 32];

        Array.Copy(_chainId, 0, SequencerSignedData, 32, 32);
        Array.Copy(KeccakHash.ComputeHashBytes(payloadData), 0, SequencerSignedData, 64, 32);
        byte[] signedHash = KeccakHash.ComputeHashBytes(SequencerSignedData);

        Span<byte> publicKey = stackalloc byte[65];
        bool success = SpanSecP256k1.RecoverKeyFromCompact(
            publicKey,
            signedHash,
            signature[..64],
            signature[64],
            false);

        // TODO verify that publicKeys match
        return success;
    }

    private bool AlreadySeen(ExecutionPayloadV3 payload)
    {
        // TODO
        return false;
    }
}
