// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;

namespace Nethermind.Facade;

public class CallOutput
{
    public string? Error { get; set; }

    public byte[] OutputData { get; set; } = [];

    public long GasSpent { get; set; }
    public long OperationGas { get; set; }

    public bool InputError { get; set; }

    public bool ExecutionReverted { get; set; }

    public AccessList? AccessList { get; set; }

    /// <summary>
    /// Set when the state anchored at the requested block was no longer available
    /// (e.g. concurrently pruned). RPC callers translate this into a <c>ResourceUnavailable</c> error.
    /// </summary>
    public bool StateUnavailable { get; set; }

    public static CallOutput NoStateForBlock(BlockHeader header) => new()
    {
        Error = $"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}",
        InputError = true,
        StateUnavailable = true,
    };
}
