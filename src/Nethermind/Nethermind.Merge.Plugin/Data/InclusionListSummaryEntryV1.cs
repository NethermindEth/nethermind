// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Represents an entry in the inclusion list summary.
/// <seealso cref="https://github.com/michaelneuder/execution-apis/pull/1"/>.
/// </summary>
public class InclusionListSummaryEntryV1
{

    public InclusionListSummaryEntryV1(Address address, ulong nonce)
    {
        Address = address;
        Nonce = nonce;
    }

    public Address Address { get; set; }
    public ulong Nonce { get; set; }
}
