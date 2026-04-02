// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Serialization.Ssz;

namespace Ethereum.Ssz.Test;

[SszSerializable]
public struct SingleFieldTestStruct
{
    public byte A { get; set; }
}

[SszSerializable]
public struct SmallTestStruct
{
    public ushort A { get; set; }
    public ushort B { get; set; }
}

[SszSerializable]
public struct FixedTestStruct
{
    public byte A { get; set; }
    public ulong B { get; set; }
    public uint C { get; set; }
}

[SszSerializable]
public struct VarTestStruct
{
    public ushort A { get; set; }

    [SszList(1024)]
    public ushort[]? B { get; set; }

    public byte C { get; set; }
}

[SszSerializable]
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

[SszSerializable]
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
