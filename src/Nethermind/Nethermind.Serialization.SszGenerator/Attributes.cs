// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#pragma warning disable IDE0130 // Generated code expects these attributes under Nethermind.Serialization.Ssz
namespace Nethermind.Serialization.Ssz;
#pragma warning restore IDE0130

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszContainerAttribute(bool isCollectionItself = false) : Attribute
{
    public bool IsCollectionItself { get; } = isCollectionItself;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public class SszCompatibleUnionAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public class SszFieldAttribute(int index) : Attribute
{
    public int Index { get; } = index;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszListAttribute(ulong limit) : Attribute
{
    public ulong Limit { get; } = limit;
    public ulong AlternateLimit { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public class SszProgressiveListAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public class SszVectorAttribute(int length) : Attribute
{
    public int Length { get; } = length;
    public int AlternateLength { get; set; }
}

/// <summary>Selects alternate SSZ dimensions using a static boolean member, fixed before any codec is used.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public class SszPresetAttribute(Type selectorType, string selectorMember) : Attribute
{
    public Type SelectorType { get; } = selectorType;
    public string SelectorMember { get; } = selectorMember;
}

[AttributeUsage(AttributeTargets.Property)]
public class SszProgressiveBitlistAttribute : Attribute;
