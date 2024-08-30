// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.State;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents a chunk of <see cref="Instruction"/>s that is optimized and ready to be run in an efficient manner.
/// </summary>
///
interface InstructionChunk
{
    byte[] Pattern { get; }
    void Invoke<T>(EvmState vmState, IWorldState worldState, IReleaseSpec spec, ref int programCounter,
        ref long gasAvailable,
        ref EvmStack<T> stack) where T : struct, IIsTracing;

    long GasCost(EvmState vmState, IReleaseSpec spec);
}
