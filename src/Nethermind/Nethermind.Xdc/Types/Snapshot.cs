// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types;

public class Snapshot(long number, Hash256 hash, Address[] masterNodes, Address[] penalizedNodes)
{
    public long BlockNumber { get; set; } = number;
    public Hash256 HeaderHash { get; set; } = hash;
    public Address[] MasterNodes { get; set; } = masterNodes;
    public Address[] PenalizedNodes { get; set; } = penalizedNodes;
}
