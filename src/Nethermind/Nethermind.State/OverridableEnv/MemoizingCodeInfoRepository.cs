// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Answers repeated code lookups from a <see cref="ResolvedCodeMemo"/> for the rest of an env scope.
/// </summary>
/// <remarks>
/// Only for an env whose scope runs one transaction, possibly re-run from the same state, as the single-call env of
/// eth_call, eth_estimateGas and eth_createAccessList does. State overrides are applied before that transaction
/// runs. After that, an address's code can change only through <see cref="InsertCode"/> (CREATE, CREATE2, a create
/// transaction) or <see cref="SetDelegation"/> (EIP-7702), and both keep the address out of the memo for the rest of
/// the scope. SELFDESTRUCT removes code only at the end of the transaction, or, since Cancun, only from accounts
/// created in it, which went through InsertCode. Precompiles and delegation designators are never remembered. Every
/// lookup the memo answers skips the inner repositories' override checks, code-hash read and cache probe, which a
/// contract-heavy call repeats on every CALL, STATICCALL and EXTCODE* to the same address.
/// </remarks>
public sealed class MemoizingCodeInfoRepository(ICodeInfoRepository codeInfoRepository, ResolvedCodeMemo memo) : ICodeInfoRepository
{
    public bool IsCodeOverridable => codeInfoRepository.IsCodeOverridable;

    public CodeInfo GetCachedCodeInfo(Address codeSource, bool followDelegation, IReleaseSpec vmSpec, out Address? delegationAddress)
    {
        // Precompiles are never remembered, so they skip the probe as well.
        if (codeSource.CouldBePrecompile())
            return codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

        if (memo.TryGet(codeSource, out CodeInfo? remembered))
        {
            // Counted like the inner repository's own memo and LRU hits.
            Nethermind.Evm.Metrics.IncrementCodeDbCache();
            delegationAddress = null;
            return remembered;
        }

        CodeInfo resolved = codeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);
        if (delegationAddress is null &&
            resolved.Precompile is null &&
            !ICodeInfoRepository.TryGetDelegatedAddress(resolved.CodeSpan, out _))
        {
            memo.Remember(codeSource, resolved);
        }

        return resolved;
    }

    public IPrecompile? GetPrecompile(Address codeSource, IReleaseSpec vmSpec) =>
        codeInfoRepository.GetPrecompile(codeSource, vmSpec);

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
    {
        memo.Forget(codeOwner);
        codeInfoRepository.InsertCode(code, codeOwner, spec);
    }

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
    {
        memo.Forget(authority);
        codeInfoRepository.SetDelegation(codeSource, authority, spec);
    }

    public bool TryGetDelegation(Address address, IReleaseSpec spec, [NotNullWhen(true)] out Address? delegatedAddress) =>
        codeInfoRepository.TryGetDelegation(address, spec, out delegatedAddress);
}
