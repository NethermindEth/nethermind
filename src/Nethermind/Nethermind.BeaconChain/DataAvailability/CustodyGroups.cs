// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Pure EIP-7594 custody derivation helpers (das-core.md / p2p-interface.md, Fulu). No I/O, no
/// state: every result is a deterministic function of its inputs alone, so any party can verify it.
/// </summary>
public static class CustodyGroups
{
    /// <summary>
    /// <c>get_custody_groups(node_id, custody_group_count)</c>: the distinct, sorted custody groups
    /// a node with <paramref name="nodeId"/> is responsible for, out of
    /// <see cref="Eip7594DasConstants.NumberOfCustodyGroups"/>.
    /// </summary>
    /// <param name="nodeId">
    /// The 32-byte <c>node_id</c>, already encoded per SSZ's Uint256 wire convention (little-endian;
    /// confirmed against <c>bytes_to_uint64</c>'s <c>ENDIANNESS = 'little'</c> in
    /// consensus-specs/specs/phase0/beacon-chain.md). How a raw discv5 NodeID is mapped onto this
    /// value is a separate, unverified concern for the caller - see 'unresolved' in the delivering
    /// task report.
    /// </param>
    /// <param name="custodyGroupCount">
    /// The number of groups to select; must not exceed <see cref="Eip7594DasConstants.NumberOfCustodyGroups"/>.
    /// </param>
    public static ulong[] GetCustodyGroups(Hash256 nodeId, ulong custodyGroupCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(custodyGroupCount, Eip7594DasConstants.NumberOfCustodyGroups);

        if (custodyGroupCount == Eip7594DasConstants.NumberOfCustodyGroups)
        {
            ulong[] all = new ulong[Eip7594DasConstants.NumberOfCustodyGroups];
            for (ulong i = 0; i < Eip7594DasConstants.NumberOfCustodyGroups; i++)
            {
                all[i] = i;
            }
            return all;
        }

        Span<byte> currentId = stackalloc byte[32];
        nodeId.Bytes.CopyTo(currentId);

        // NUMBER_OF_CUSTODY_GROUPS (128) is small and fixed: a presence array is simpler and
        // allocation-free compared to a HashSet, and membership/sort fall out of one final scan.
        Span<bool> present = stackalloc bool[(int)Eip7594DasConstants.NumberOfCustodyGroups];
        int found = 0;
        Span<byte> hash = stackalloc byte[32];

        while (found < (int)custodyGroupCount)
        {
            SHA256.HashData(currentId, hash);
            ulong candidate = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]) % Eip7594DasConstants.NumberOfCustodyGroups;

            if (!present[(int)candidate])
            {
                present[(int)candidate] = true;
                found++;
            }

            IncrementWithWraparound(currentId);
        }

        ulong[] result = new ulong[custodyGroupCount];
        int position = 0;
        for (int group = 0; group < present.Length; group++)
        {
            if (present[group])
            {
                result[position++] = (ulong)group;
            }
        }

        return result;
    }

    /// <summary>
    /// <c>compute_columns_for_custody_group(custody_group)</c>: the columns a node custodying
    /// <paramref name="custodyGroup"/> must download and serve.
    /// </summary>
    /// <remarks>
    /// On mainnet, <c>NUMBER_OF_COLUMNS / NUMBER_OF_CUSTODY_GROUPS == 1</c>, so this happens to be a
    /// single-element array with <c>columns[0] == custodyGroup</c> - a preset coincidence, not a
    /// guarantee, so the general division is computed rather than assumed.
    /// </remarks>
    public static ulong[] ComputeColumnsForCustodyGroup(ulong custodyGroup)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(custodyGroup, Eip7594DasConstants.NumberOfCustodyGroups);

        ulong columnsPerGroup = (ulong)Eip7594DasConstants.NumberOfColumns / Eip7594DasConstants.NumberOfCustodyGroups;
        ulong[] columns = new ulong[columnsPerGroup];
        for (ulong i = 0; i < columnsPerGroup; i++)
        {
            columns[i] = Eip7594DasConstants.NumberOfCustodyGroups * i + custodyGroup;
        }

        return columns;
    }

    /// <summary><c>compute_subnet_for_data_column_sidecar(column_index)</c>.</summary>
    public static ulong ComputeSubnetForDataColumnSidecar(ulong columnIndex) =>
        columnIndex % Eip7594DasConstants.DataColumnSidecarSubnetCount;

    /// <summary>Adds one to a little-endian unsigned integer, wrapping to zero on overflow (mod 2^256).</summary>
    private static void IncrementWithWraparound(Span<byte> littleEndianValue)
    {
        for (int i = 0; i < littleEndianValue.Length; i++)
        {
            if (++littleEndianValue[i] != 0)
            {
                return;
            }
        }
        // Every byte carried through and wrapped to 0: the value was UINT256_MAX, now 0.
    }
}
