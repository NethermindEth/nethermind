// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public interface ICodeInfo
{
    byte[] MachineCode { get; }
    IPrecompile? Precompile { get; }
    bool IsPrecompile => Precompile is not null;
    ReadOnlyMemory<byte> TypeSection => Memory<byte>.Empty;
    ReadOnlyMemory<byte> CodeSection => MachineCode;
    ReadOnlyMemory<byte> DataSection => Memory<byte>.Empty;
    int SectionOffset(int _) => 0;
    bool ValidateJump(int destination, bool isSubroutine);
}
