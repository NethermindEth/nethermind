// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.State.OverridableEnv;

/// <summary>The code and precompile overrides of one overridable env.</summary>
/// <remarks>
/// The env's <see cref="OverridableCodeInfoRepository"/> writes the overrides. The per-transaction
/// <see cref="MovedPrecompileCodeInfoRepository"/> of block access list execution reads the precompile moves and the
/// code overrides at precompile addresses. Dispatch and plain bytecode views share the same code bytes; both are
/// prepared when an override is installed, not allocated on each call or delegation lookup.
/// </remarks>
public sealed class CodeOverrideStore
{
    internal Dictionary<Address, (CodeInfo codeInfo, CodeInfo delegatedCodeInfo, ValueHash256 codeHash)> Code { get; } = [];
    internal Dictionary<Address, CodeInfo> Precompiles { get; } = [];
}
