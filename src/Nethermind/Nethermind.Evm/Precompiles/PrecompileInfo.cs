// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class PrecompileInfo(IPrecompile precompile) : ICodeInfo
{
    public ReadOnlyMemory<byte> MachineCode => Array.Empty<byte>();
    public IPrecompile? Precompile { get; } = precompile;

    public bool IsPrecompile => true;
    public bool IsEmpty => false;
}
