// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Disables EIP-158 empty-account deletion so state-override commits cannot remove accounts
/// that carry storage but no code/balance/nonce, which would make EIP-7610 CREATE collision
/// checks miss existing storage.
/// </summary>
internal sealed class NoEip158Spec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public override bool IsEip158Enabled => false;
}
