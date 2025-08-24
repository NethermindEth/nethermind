// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.Config;
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;

namespace Nethermind.Evm.CodeAnalysis;

public sealed class PrecompileInfo(IPrecompile precompile) : ICodeInfo
{
    public ReadOnlyMemory<byte> Code => Array.Empty<byte>();
    ReadOnlySpan<byte> ICodeInfo.CodeSpan => Code.Span;
    public IPrecompile? Precompile { get; } = precompile;

    public bool IsPrecompile => true;
    public bool IsEmpty => false;

    public ValueHash256? CodeHash => throw new NotImplementedException();

    public IlInfo IlMetadata => throw new NotImplementedException();

    public void NoticeExecution(IVMConfig vmConfig, ILogger logger, IReleaseSpec spec)
    {
        return;
    }
}
