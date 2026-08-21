// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL.P2P;

public interface IPayloadDecoder
{
    /// <summary>
    /// Decodes an execution payload received over the CL P2P network.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed payload.</exception>
    ExecutionPayloadV3 DecodePayload(ReadOnlySpan<byte> data);
    byte[] EncodePayload(ExecutionPayloadV3 payload);
}
