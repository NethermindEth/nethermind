// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime;

namespace Nethermind.Serialization.Ssz;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszSerializableAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public class SszListAttribute(int limit = 0) : Attribute
{
    public int Limit { get; } = limit;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszVectorAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}
