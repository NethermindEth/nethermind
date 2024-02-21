// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateCallResult
{
    public ulong Type =>
        (ulong)(Status switch
        {
            StatusCode.Success => ResultType.Success,
            StatusCode.Failure when ReturnData is not null => ResultType.Failure,
            _ => ResultType.Invalid,
        });

    public ulong Status { get; set; }
    public byte[]? ReturnData { get; set; }
    public ulong? GasUsed { get; set; }
    public Error? Error { get; set; }
    public IEnumerable<Log> Logs { get; set; } = Enumerable.Empty<Log>();
}
