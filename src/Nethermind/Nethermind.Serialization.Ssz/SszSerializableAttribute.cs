// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

/// <summary>
/// Marks a type as an SSZ container.
/// </summary>
/// <param name="isCollectionItself">
/// Whether a single collection property should be exposed as the type value itself.
/// </param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszContainerAttribute(bool isCollectionItself = false) : Attribute
{
    public bool IsCollectionItself { get; } = isCollectionItself;
}

/// <summary>
/// Marks a type as an SSZ compatible union as defined by EIP-7916.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszCompatibleUnionAttribute : Attribute;

/// <summary>
/// Assigns a stable field index to a progressive-container member.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SszFieldAttribute(int index) : Attribute
{
    public int Index { get; } = index;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszListAttribute(int limit) : Attribute
{
    public int Limit { get; } = limit;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszProgressiveListAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public class SszVectorAttribute(int length) : Attribute
{
    public int Length { get; } = length;
}

/// <summary>
/// Marks a <see cref="System.Collections.BitArray"/> property as a progressive bitlist.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SszProgressiveBitlistAttribute : Attribute;
