// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// Beacon-api validator status classification (ethereum/beacon-APIs
/// <c>get_validator_status</c>): pending/active/exited/withdrawal, each with the sub-state the spec
/// derives from the validator record and the epoch asked about.
/// </summary>
internal static class ValidatorStatus
{
    public static string Classify(Validator validator, ulong epoch)
    {
        if (validator.ActivationEpoch > epoch)
        {
            return validator.ActivationEligibilityEpoch == Presets.FarFutureEpoch
                ? "pending_initialized"
                : "pending_queued";
        }

        if (epoch < validator.ExitEpoch)
        {
            if (validator.ExitEpoch == Presets.FarFutureEpoch) return "active_ongoing";
            return validator.Slashed ? "active_slashed" : "active_exiting";
        }

        if (epoch < validator.WithdrawableEpoch)
        {
            return validator.Slashed ? "exited_slashed" : "exited_unslashed";
        }

        return validator.EffectiveBalance != 0 ? "withdrawal_possible" : "withdrawal_done";
    }

    /// <summary>Matches a beacon-api <c>status</c> query value, which may name a specific status or one of the broad groups ("pending"/"active"/"exited"/"withdrawal").</summary>
    public static bool MatchesFilter(string status, string filter) =>
        status.Equals(filter, StringComparison.Ordinal)
        || status.StartsWith(filter + "_", StringComparison.Ordinal);
}
