// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Container;

public enum ComponentKey
{
    NodeKey
}

public class ComponentKeyAttribute(ComponentKey key) : Attribute
{
    public ComponentKey Key { get; } = key;
}
