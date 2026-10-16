// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Specs;

public abstract partial class ForkScheduleSpecProvider
{
    /// <inheritdoc/>
    /// <remarks>A guest looks up one fork per run, so building the name index over the whole schedule costs more than a scan.
    /// The scan starts from the newest fork, the one inputs pin: each fork is its own spec type, and every new
    /// receiver type at the Name call site costs an interface dispatch resolution. Starting there also keeps the
    /// name index's rule that the last of duplicate names wins.</remarks>
    public bool TryGetForkSpec(string forkName, out IReleaseSpec? spec)
    {
        ForkSpec[] schedule = ForkSchedule;
        for (int i = schedule.Length - 1; i >= 0; i--)
        {
            if (string.Equals(schedule[i].Spec.Name, forkName, StringComparison.OrdinalIgnoreCase))
            {
                spec = schedule[i].Spec;
                return true;
            }
        }

        spec = null;
        return false;
    }
}
