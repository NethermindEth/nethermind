// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateCallResult
{
    public ulong Type =>
        Status switch
        {
            StatusCode.Success => (ulong)ResultType.Success,
            StatusCode.Failure when ReturnData is not null => (ulong)ResultType.Failure,
            _ => (ulong)ResultType.Invalid,
        };

    public ulong Status { get; set; }
    public byte[]? ReturnData { get; set; }
    public ulong? GasUsed { get; set; }
    public Error? Error { get; set; }
    public Log[] Logs { get; set; } = [];
}
