// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>Bytecode cache keyed by code hash, used by <see cref="CacheCodeInfoRepository"/> to avoid re-reading and re-parsing code from the world state.</summary>
/// <remarks>
/// The witness/stateless path injects <see cref="NoopCodeCache"/> so that every code lookup goes through the world state and
/// is captured in the generated witness; a process-wide cache hit would otherwise serve code without a world-state read. See <see cref="CodeInfoRepository"/>.
/// </remarks>
public interface ICodeCache
{
    CodeInfo? Get(in ValueHash256 codeHash);
    void Set(in ValueHash256 codeHash, CodeInfo codeInfo);
    void Clear();
}
