// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;

namespace Nethermind.Facade;

public class CallOutput
{
    /// <summary>The first frame that failed during simulation, including an invalidating VERIFY failure.</summary>
    public int? FailedFrameIndex { get; set; }

    /// <summary>Per-frame outcomes from a completed frame transaction simulation.</summary>
    public TxFrameReceipt[]? FrameReceipts { get; set; }

    public string? Error { get; set; }

    public byte[] OutputData { get; set; } = [];

    public ulong GasSpent { get; set; }
    public ulong OperationGas { get; set; }

    public bool InputError { get; set; }

    public bool ExecutionReverted { get; set; }

    public AccessList? AccessList { get; set; }
}
