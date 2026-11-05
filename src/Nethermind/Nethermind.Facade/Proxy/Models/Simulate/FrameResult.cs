// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Facade.Proxy.Models.Simulate;

/// <summary>The outcome and output of one simulated frame.</summary>
public class FrameResult
{
    public ulong Status { get; set; }
    public ulong GasUsed { get; set; }
    public ulong ExecutionGasUsed { get; set; }
    public ulong StateGasUsed { get; set; }
    public ICollection<Log> Logs { get; set; } = [];
    public byte[] ReturnData { get; set; } = [];
}
