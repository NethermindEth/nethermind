// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

// Validates p2p "blocks" messages
public interface IP2PBlockValidator
{
    ValidityStatus Validate(ExecutionPayloadV3 payload, P2PTopic topic);
    ValidityStatus ValidateSignature(byte[] payloadData, byte[] signature);
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