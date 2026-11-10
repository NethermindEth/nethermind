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
    /// <summary>Stores <paramref name="codeInfo"/> under <paramref name="codeHash"/>.</summary>
    /// <remarks>
    /// A cache that stores must stamp <paramref name="codeHash"/> on <paramref name="codeInfo"/>
    /// (<see cref="CodeInfo.StampCodeHash"/>): <see cref="CacheCodeInfoRepository"/>'s last-resolved memo matches on it.
    /// One that stores nothing, like <see cref="NoopCodeCache"/>, must not, or the memo would serve code without reading
    /// it through the world state.
    /// </remarks>
    void Set(in ValueHash256 codeHash, CodeInfo codeInfo);
    void Clear();
}
