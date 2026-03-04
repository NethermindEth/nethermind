// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Evm;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class Error
{
    public int Code { get; set; }
    public string? Message { get; set; }
    public byte[]? Data { get; set; }

    [JsonIgnore]
    public EvmExceptionType EvmException { get; set; }
}
