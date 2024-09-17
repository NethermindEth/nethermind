// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base.Interfaces;
using System.Collections.Generic;
using System.Numerics;

namespace Ethereum.Test.Base;
public class Result
{
    public string Fork { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class VectorTest
{
    public byte[] Code { get; set; }
}

public class EofTest : IEthereumTest
{
    public string Name { get; set; }
    public VectorTest Vector { get; set; }
    public string? Category { get; set; }
    public string? LoadFailure { get; set; }
    public Result Result { get; internal set; }
}
