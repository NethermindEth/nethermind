// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types;

public class Snapshot(ulong number, Hash256 hash, Address[] masterNodes)
{
    public ulong BlockNumber { get; set; } = number;
    public Hash256 HeaderHash { get; set; } = hash;
    public Address[] NextEpochCandidates { get; set; } = masterNodes;
}
