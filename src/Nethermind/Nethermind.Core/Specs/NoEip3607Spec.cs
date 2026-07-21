// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Wraps a release spec and disables EIP-3607 so that contracts can act as transaction senders
/// in simulated calls without being rejected by the sender-has-deployed-code check.
/// </summary>
internal sealed class NoEip3607Spec(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public override bool IsEip3607Enabled => false;
}
