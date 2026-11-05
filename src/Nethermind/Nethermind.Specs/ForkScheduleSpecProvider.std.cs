// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public abstract partial class ForkScheduleSpecProvider
{
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec) => Forks.TryGetValue(forkName, out spec);
}
