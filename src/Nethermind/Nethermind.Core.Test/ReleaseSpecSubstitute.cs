// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using NSubstitute;

namespace Nethermind.Core.Test;

public static class ReleaseSpecSubstitute
{
    public static IReleaseSpec Create()
    {
        IReleaseSpec sub = Substitute.For<IReleaseSpec>();
        sub.GasCosts.Returns(new SpecGasCosts(sub));
        return sub;
    }
}
