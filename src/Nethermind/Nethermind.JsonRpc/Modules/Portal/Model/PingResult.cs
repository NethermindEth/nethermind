// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Portal;

public class PingResult
{
    public int EnrReq { get; set; }
    public UInt256 DataRadius { get; set; }
}
