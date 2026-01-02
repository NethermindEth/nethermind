// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Ssz;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszSerializableAttribute(bool isCollectionItself = false) : Attribute
{
    public bool IsCollectionItself { get; } = isCollectionItself;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszListAttribute(int limit) : Attribute
{
    public int Limit { get; } = limit;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszVectorAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}
