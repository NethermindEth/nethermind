// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Eip2930;

namespace Nethermind.Facade;

public class CallOutput
{
    public string? Error { get; set; }

    public byte[] OutputData { get; set; }

    public long GasSpent { get; set; }

    public bool InputError { get; set; }

    public AccessList? AccessList { get; set; }
}
