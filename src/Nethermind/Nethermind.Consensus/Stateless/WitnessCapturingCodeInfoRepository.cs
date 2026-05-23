// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// <see cref="ICodeInfoRepository"/> decorator that, when a witness capture is in progress,
/// ensures every non-empty bytecode accessed through <see cref="GetCachedCodeInfo"/> is
/// recorded in the active <see cref="WitnessGeneratingWorldState"/>.
/// </summary>
public sealed class WitnessCapturingCodeInfoRepository(
    ICodeInfoRepository inner,
    WitnessCapturingWorldStateProxy proxy) : ICodeInfoRepository
{
    public CodeInfo GetCachedCodeInfo(
        Address codeSource,
        bool followDelegation,
        IReleaseSpec vmSpec,
        out Address? delegationAddress)
    {
        CodeInfo codeInfo = inner.GetCachedCodeInfo(codeSource, followDelegation, vmSpec, out delegationAddress);

        if (proxy.IsActive && codeInfo.Code.Length > 0)
        {
            proxy.GetCode(delegationAddress ?? codeSource);
        }

        return codeInfo;
    }

    public void InsertCode(ReadOnlyMemory<byte> code, Address codeOwner, IReleaseSpec spec)
        => inner.InsertCode(code, codeOwner, spec);

    public void SetDelegation(Address codeSource, Address authority, IReleaseSpec spec)
        => inner.SetDelegation(codeSource, authority, spec);

    public bool TryGetDelegation(Address address, IReleaseSpec spec,
        [NotNullWhen(true)] out Address? delegatedAddress)
        => inner.TryGetDelegation(address, spec, out delegatedAddress);
}
