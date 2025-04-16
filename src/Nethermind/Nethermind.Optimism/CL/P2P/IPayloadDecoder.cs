// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL.P2P;

public interface IPayloadDecoder
{
    ExecutionPayloadV3 DecodePayload(ReadOnlySpan<byte> data);
    byte[] EncodePayload(ExecutionPayloadV3 payload);
}
