// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class SszBasicTypeAttribute(int staticLength) : Attribute
{
    public int StaticLength { get; } = staticLength;

    public bool IsRefType { get; set; }

    public string? EncodeTemplate { get; set; }

    public string? DecodeTemplate { get; set; }
}
