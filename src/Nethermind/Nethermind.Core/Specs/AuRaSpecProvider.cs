// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;


public class AuRaSpecProvider(ISpecProvider baseSpecProvider) : SpecProviderDecorator(baseSpecProvider)
{
    public override IReleaseSpec GetSpec(ForkActivation forkActivation) =>
        new AuRaReleaseSpecDecorator(base.GetSpec(forkActivation));
}

public class AuRaReleaseSpecDecorator(IReleaseSpec spec) : ReleaseSpecDecorator(spec)
{
    public override bool IsEip158IgnoredAccount(Address address) => address == Address.SystemUser;
}
