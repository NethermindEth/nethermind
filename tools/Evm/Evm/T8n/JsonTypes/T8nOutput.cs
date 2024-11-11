// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Ethereum.Test.Base;
using Nethermind.Core;

namespace Evm.T8n.JsonTypes;

public class T8nOutput
{
    public Dictionary<Address, AccountState>? Alloc { get; set; }
    public PostState? Result { get; set; }
    public byte[]? Body { get; set; }
    public string? ErrorMessage { get; set; }
    [JsonIgnore]
    public int ExitCode { get; set; }

    public T8nOutput() { }

    public T8nOutput(string errorMessage, int exitCode)
    {
        ErrorMessage = errorMessage;
        ExitCode = exitCode;
    }

    public bool IsEmpty()
    {
        return Result is null && Body is null && Alloc is null;
    }
}
