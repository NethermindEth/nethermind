// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

public class AuraSpecProvider(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public override bool IsEip158IgnoredAccount(Address address) => address == Address.SystemUser;
}
