// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszSerializableAttribute : Attribute;

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
