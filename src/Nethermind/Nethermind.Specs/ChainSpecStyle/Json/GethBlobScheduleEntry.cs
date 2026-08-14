// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.ChainSpecStyle.Json;

public class GethBlobScheduleEntry
{
    public ulong Target { get; set; }

    public ulong Max { get; set; }

    public ulong BaseFeeUpdateFraction { get; set; }
}
