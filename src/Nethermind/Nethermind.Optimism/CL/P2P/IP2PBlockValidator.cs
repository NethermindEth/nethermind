// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL.P2P;

// Validates p2p "blocks" messages
public interface IP2PBlockValidator
{
    ValidityStatus Validate(ExecutionPayloadV3 payload, P2PTopic topic);
    ValidityStatus ValidateSignature(ReadOnlySpan<byte> payloadData, Span<byte> signature);
    ValidityStatus IsBlockNumberPerHeightLimitReached(ExecutionPayloadV3 payload);
}

public enum ValidityStatus
{
    Valid,
    Reject
}

public enum P2PTopic
{
    BlocksV1,
    BlocksV2,
    BlocksV3
}
