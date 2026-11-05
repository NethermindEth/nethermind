// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.State.OverridableEnv;

/// <summary>
/// Keeps code adopted by EIP-8298 <c>SETCODEFROM</c> out of the <see cref="ResolvedCodeMemo"/>.
/// </summary>
/// <remarks>
/// <c>SETCODEFROM</c> writes the executing account's code straight to the world state rather than through the code
/// repository, so <see cref="MemoizingCodeInfoRepository"/> never sees it and would keep answering with the code the
/// account had before.
/// </remarks>
public sealed class ResolvedCodeForgettingWorldState(IWorldState state, ResolvedCodeMemo memo) : WorldStateDecorator(state)
{
    public override bool AdoptCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec)
    {
        memo.Forget(address);
        return base.AdoptCode(address, in codeHash, code, spec);
    }
}
