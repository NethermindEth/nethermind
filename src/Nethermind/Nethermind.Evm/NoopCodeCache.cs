// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>An <see cref="ICodeCache"/> that stores nothing, forcing every code lookup through the world state.</summary>
/// <remarks>Used by witness generation and stateless execution so touched bytecodes are recorded rather than served from a process-wide cache.</remarks>
public sealed class NoopCodeCache : ICodeCache
{
    public static readonly NoopCodeCache Instance = new();

    private NoopCodeCache() { }

    public CodeInfo? Get(in ValueHash256 codeHash) => null;

    public void Set(in ValueHash256 codeHash, CodeInfo codeInfo) { }

    public void Clear() { }
}
