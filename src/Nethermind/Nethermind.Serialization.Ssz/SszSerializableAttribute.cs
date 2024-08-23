// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Ssz;

public class SszSerializableAttribute : Attribute;
public class SszLimitAttribute(int limit) : Attribute
{
    public int Limit { get; } = limit;
}
