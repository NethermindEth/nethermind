// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Thin <see cref="ICodeInfoRepository"/> decorator that routes every call to a non-caching
/// <see cref="CodeInfoRepository"/> it owns when <paramref name="useNonCaching"/> returns <c>true</c>,
/// and to the wrapped cached repository otherwise. The predicate is evaluated per call so the choice
/// tracks the live witness-execution state rather than being fixed at construction.
/// </summary>
/// <remarks>
/// Witness execution requires every bytecode/code-hash lookup to flow through <see cref="IWorldState"/>
/// (so a recording world state observes it, or so stateless execution stays isolated to the witness);
/// the process-wide static code cache used by the inner repository would short-circuit those reads.
/// The non-caching repository is built inside this decorator (rather than resolved from DI) so no other
/// DI consumer can pick it up and accidentally bypass the cache for non-witness blocks.
/// </remarks>
public sealed class CodeInfoRepositoryProxy(
    ICodeInfoRepository inner,
    IWorldState worldState,
    IPrecompileProvider precompileProvider,
    Func<bool> useNonCaching) : ICodeInfoRepository
{
    private readonly ICodeInfoRepository _inner = inner;
    private readonly CodeInfoRepository _nonCached = new(worldState, precompileProvider);

    private ICodeInfoRepository Current => useNonCaching() ? _nonCached : _inner;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
        => Current.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
        => Current.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
        => Current.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress)
        => Current.TryGetDelegation(address, spec, out delegatedAddress);
}
