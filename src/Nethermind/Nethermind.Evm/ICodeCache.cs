// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>Bytecode cache keyed by code hash, used by <see cref="CacheCodeInfoRepository"/> to avoid re-reading and re-parsing code from the world state.</summary>
/// <remarks>
/// Witness <em>generation</em> injects <see cref="NoopCodeCache"/> so that every code lookup goes through the world state and is
/// captured in the witness. Stateless <em>validation</em> may cache, being keyed by code hash exactly as the witness stores code.
/// See <see cref="CodeInfoRepository"/>.
/// </remarks>
public interface ICodeCache
{
    CodeInfo? Get(in ValueHash256 codeHash);
    void Set(in ValueHash256 codeHash, CodeInfo codeInfo);
    void Clear();
}
