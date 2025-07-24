// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base.Interfaces;
using Nethermind.Evm.EvmObjectFormat;

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
    public ValidationStrategy ContainerKind { get; set; }
}

public class EofTest : EthereumTest
{
    public VectorTest Vector { get; set; }
    public Result Result { get; internal set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public string? Spec { get; set; }
}
