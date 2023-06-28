// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class MultiCallCallResult
{
    public ResultType Type
    {
        get
        {
            if (Error != null) return ResultType.Failure;

            if (Logs != null) return ResultType.Success;

            return ResultType.Invalid;
        }
    }

    public Log[]? Logs { get; set; }
    public string Status { get; set; }
    public byte[]? Return { get; set; }
    public Error? Error { get; set; }
    public ulong? GasUsed { get; set; }
}
