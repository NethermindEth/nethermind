// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class Error
{
    public int Code { get; set; }
    public string Message { get; set; }
    public string Data { get; set; }
}
