// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateCallResult
{
    public short Type =>
        Status switch
        {
            StatusCode.Success => (short)ResultType.Success,
            StatusCode.Failure when ReturnData is not null => (short)ResultType.Failure,
            _ => (short)ResultType.Invalid,
        };

    public short Status { get; set; }
    public byte[]? ReturnData { get; set; }
    public ulong? GasUsed { get; set; }
    public Error? Error { get; set; }
    public Log[] Logs { get; set; } = [];
}
