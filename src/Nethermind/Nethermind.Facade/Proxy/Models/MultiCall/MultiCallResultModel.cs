// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class MultiCallResultModel
{
    public ResultType Type
    {
        get
        {
            if (Logs != null)
            {
                return ResultType.Success;
            }

            if (Return != null && Error != null)
            {
                return ResultType.Failure;
            }
            return ResultType.Invalid;
        }
    }

    public string Status { get; set; }
    public byte[]? Return { get; set; }
    public ulong? GasUsed { get; set; }

    public Error? Error { get; set; }
    public Log[]? Logs { get; set; }

}
