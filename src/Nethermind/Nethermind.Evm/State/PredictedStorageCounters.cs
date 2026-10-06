// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.State;

/// <summary>Experiment only: what the predicted storage trees came to, summed over the process.</summary>
public static class PredictedStorageCounters
{
    public static long Built, BuiltWrites, BuildTicks, Adopted, Unclaimed, StaleBase, Late;
}
