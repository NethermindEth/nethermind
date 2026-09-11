// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public interface IInstructionTracingFilter
{
    UInt256 InstructionMask { get; }
}
