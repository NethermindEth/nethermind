// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>
/// One block's entries of the spec's <c>store.payload_timeliness_vote</c> and <c>store.payload_data_availability_vote</c>:
/// a vote per PTC seat, <see langword="null"/> until that seat's member votes (specs/gloas/fork-choice.md).
/// </summary>
internal sealed class PtcVotes
{
    public bool?[] Timeliness { get; } = new bool?[Presets.PtcSize];
    public bool?[] DataAvailability { get; } = new bool?[Presets.PtcSize];

    /// <summary>
    /// The vote count of <c>payload_timeliness</c> and <c>payload_data_availability</c>: strictly more than
    /// <c>PAYLOAD_TIMELY_THRESHOLD</c> (= <c>DATA_AVAILABILITY_TIMELY_THRESHOLD</c> = <c>PTC_SIZE // 2</c>) seats voted <paramref name="value"/>.
    /// </summary>
    public static bool HasQuorum(bool?[] votes, bool value)
    {
        ulong count = 0;
        foreach (bool? vote in votes)
        {
            if (vote == value) count++;
        }

        return count > Presets.PtcSize / 2;
    }
}
