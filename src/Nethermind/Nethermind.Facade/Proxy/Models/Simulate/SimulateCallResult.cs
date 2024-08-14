// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateCallResult
{
    public ulong Status { get; set; }
    public byte[]? ReturnData { get; set; }
    public ulong? GasUsed { get; set; }
    public Error? Error { get; set; }
    public IEnumerable<Log> Logs { get; set; } = Enumerable.Empty<Log>();
}
