// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base;

public class VectorTestJson
{
    public string Code { get; set; }
    public string ContainerKind { get; set; }
    public Dictionary<string, TestResultJson> Results { get; set; }
}
