// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Wraps a release spec and disables EIP-158 empty-account deletion so that state-override
/// commits do not spuriously delete accounts whose code/nonce were zeroed while storage remains.
/// </summary>
internal sealed class NoEip158Spec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public override bool IsEip158Enabled => false;
}
