// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

public interface IPayloadDecoder
{
    ExecutionPayloadV3 DecodePayload(byte[] data);
    byte[] EncodePayload(ExecutionPayloadV3 payload);
}
