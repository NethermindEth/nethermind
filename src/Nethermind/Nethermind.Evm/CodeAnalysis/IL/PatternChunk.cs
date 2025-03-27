// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal interface IPatternChunk : InstructionChunk
{
    long GasCost(EvmState vmState, IReleaseSpec spec);
    byte[] Pattern { get; }
}
