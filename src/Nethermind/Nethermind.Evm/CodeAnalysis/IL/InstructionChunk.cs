// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents a chunk of <see cref="Instruction"/>s that is optimized and ready to be run in an efficient manner.
/// </summary>
///
interface InstructionChunk
{
    static byte[] Pattern { get; }
    void Invoke<T>(EvmState vmState, IReleaseSpec spec, ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack) where T : struct, IIsTracing;
}
