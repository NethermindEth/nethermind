// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;

namespace Nethermind.Stateless.Execution.IO;

internal static partial class InputDecoder
{
    /// <remarks>Only where <see cref="FirstTouchMemoryFlag"/> is on, which assumes the input lies in writable memory that
    /// outlives the run, as on SP1. The input's second offset locates the witness, and the witness's first its state list.
    /// Decoding checks both before it decodes a node, so offsets out of range only leave the nodes copied.</remarks>
    static partial void PlaceWitnessNodesInInput(ReadOnlySpan<byte> data)
    {
        if (!FirstTouchMemoryFlag.IsActive || data.Length < 2 * sizeof(uint))
            return;

        uint witness = BinaryPrimitives.ReadUInt32LittleEndian(data[sizeof(uint)..]);
        if (witness > data.Length - sizeof(uint))
            return;

        ulong stateList = witness + (ulong)BinaryPrimitives.ReadUInt32LittleEndian(data[(int)witness..]);
        if (stateList <= (ulong)data.Length)
            WitnessNode.PlaceInInput(data, (int)stateList);
    }
}
