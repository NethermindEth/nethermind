// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Serialization.Ssz;

namespace Ethereum.Ssz.Test;

[SszContainer]
public struct SingleFieldTestStruct
{
    public byte A { get; set; }
}

[SszContainer]
public struct SmallTestStruct
{
    public ushort A { get; set; }
    public ushort B { get; set; }
}

[SszContainer]
public struct FixedTestStruct
{
    public byte A { get; set; }
    public ulong B { get; set; }
    public uint C { get; set; }
}

[SszContainer]
public struct VarTestStruct
{
    public ushort A { get; set; }

    [SszList(1024)]
    public ushort[]? B { get; set; }

    public byte C { get; set; }
}

[SszContainer]
public struct ComplexTestStruct
{
    public ushort A { get; set; }

    [SszList(128)]
    public ushort[]? B { get; set; }

    public byte C { get; set; }

    [SszList(256)]
    public byte[]? D { get; set; }

    public VarTestStruct E { get; set; }

    [SszVector(4)]
    public FixedTestStruct[]? F { get; set; }

    [SszVector(2)]
    public VarTestStruct[]? G { get; set; }
}

[SszContainer]
public struct ProgressiveTestStruct
{
    [SszProgressiveList]
    public byte[]? A { get; set; }

    [SszProgressiveList]
    public ulong[]? B { get; set; }

    [SszProgressiveList]
    public SmallTestStruct[]? C { get; set; }

    [SszProgressiveList]
    public ProgressiveVarTestStructList[]? D { get; set; }
}

[SszContainer(true)]
public struct ProgressiveVarTestStructList
{
    [SszProgressiveList]
    public VarTestStruct[]? Items { get; set; }
}

[SszContainer]
public struct BitsStruct
{
    [SszList(5)]
    public BitArray? A { get; set; }

    [SszVector(2)]
    public BitArray? B { get; set; }

    [SszVector(1)]
    public BitArray? C { get; set; }

    [SszList(6)]
    public BitArray? D { get; set; }

    [SszVector(8)]
    public BitArray? E { get; set; }
}

[SszContainer]
public struct ProgressiveBitsStruct
{
    [SszVector(256)]
    public BitArray? A { get; set; }

    [SszList(256)]
    public BitArray? B { get; set; }

    [SszProgressiveBitlist]
    public BitArray? C { get; set; }

    [SszVector(257)]
    public BitArray? D { get; set; }

    [SszList(257)]
    public BitArray? E { get; set; }

    [SszProgressiveBitlist]
    public BitArray? F { get; set; }

    [SszVector(1280)]
    public BitArray? G { get; set; }

    [SszList(1280)]
    public BitArray? H { get; set; }

    [SszProgressiveBitlist]
    public BitArray? I { get; set; }

    [SszVector(1281)]
    public BitArray? J { get; set; }

    [SszList(1281)]
    public BitArray? K { get; set; }

    [SszProgressiveBitlist]
    public BitArray? L { get; set; }
}
