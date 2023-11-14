// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IL;

/// <summary>
/// Represents a chunk of <see cref="Instruction"/>s that is optimized and ready to be run in an efficient manner.
/// </summary>
delegate void InstructionChunk(EvmState vmState, IReleaseSpec spec, ref int programCounter,
    ref long gasAvailable,
    ref EvmStack<VirtualMachine.NotTracing> stack);
