// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Fulu per-slot column sampling (das-core.md "custody sampling", which superseded the older
/// probabilistic peer-sampling design) and the data-availability call it feeds: beyond the columns a
/// node custodies long-term, it samples further columns every slot to check block availability
/// without ever holding the whole 128-column matrix.
/// </summary>
public static class DataAvailabilitySampling
{
    /// <summary>
    /// <c>sampling_size = max(SAMPLES_PER_SLOT, custody_group_count)</c>: a node whose own custody
    /// already exceeds <see cref="Eip7594DasConstants.SamplesPerSlot"/> samples exactly its custody,
    /// never fewer than it already downloads.
    /// </summary>
    public static ulong GetSamplingSize(ulong custodyGroupCount) =>
        Math.Max(Eip7594DasConstants.SamplesPerSlot, custodyGroupCount);

    /// <summary>
    /// The distinct, sorted columns this node downloads and checks every slot: every column of every
    /// custody group in <c>get_custody_groups(node_id, sampling_size)</c>. By construction of
    /// <see cref="CustodyGroups.GetCustodyGroups"/> (both walks start at the same node id and take the
    /// first N distinct hits in the same order), the node's own long-term custody columns for
    /// <paramref name="custodyGroupCount"/> groups are always a subset of this set - the spec's own
    /// invariant, not an incidental one.
    /// </summary>
    public static ulong[] GetColumnsToSample(Hash256 nodeId, ulong custodyGroupCount)
    {
        ulong samplingSize = GetSamplingSize(custodyGroupCount);
        ulong[] groups = CustodyGroups.GetCustodyGroups(nodeId, samplingSize);

        SortedSet<ulong> columns = [];
        foreach (ulong group in groups)
        {
            foreach (ulong column in CustodyGroups.ComputeColumnsForCustodyGroup(group))
            {
                columns.Add(column);
            }
        }

        ulong[] result = new ulong[columns.Count];
        columns.CopyTo(result);
        return result;
    }

    /// <summary>
    /// <c>data_column_sidecar</c> availability for one block: das-core.md - "Sampling is considered
    /// successful if the node manages to retrieve all selected columns." A node cannot conclude
    /// availability from zero samples, so an empty <paramref name="columnsToSample"/> is judged
    /// unavailable rather than vacuously available; in practice this cannot arise from
    /// <see cref="GetColumnsToSample"/> itself, since <see cref="GetSamplingSize"/> is never zero.
    /// </summary>
    public static bool IsSampleAvailable(IReadOnlyCollection<ulong> columnsToSample, Func<ulong, bool> isColumnHeld)
    {
        ArgumentNullException.ThrowIfNull(columnsToSample);
        ArgumentNullException.ThrowIfNull(isColumnHeld);

        if (columnsToSample.Count == 0)
        {
            return false;
        }

        foreach (ulong column in columnsToSample)
        {
            if (!isColumnHeld(column))
            {
                return false;
            }
        }

        return true;
    }
}
