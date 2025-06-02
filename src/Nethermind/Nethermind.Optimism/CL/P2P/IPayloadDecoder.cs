// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Optimism.CL.P2P;

public interface IPayloadDecoder
{
    OptimismExecutionPayloadV3 DecodePayload(ReadOnlySpan<byte> data, uint version);
    byte[] EncodePayload(OptimismExecutionPayloadV3 payload);
}
