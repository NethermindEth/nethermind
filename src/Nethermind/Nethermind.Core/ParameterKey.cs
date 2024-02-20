// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// To be used with `KeyedParameterAttribute`.
/// </summary>
public enum ParameterKey
{
    BlockStoreMaxSize,
    DbPath
}

/// <summary>
/// Used to mark parameter to be used with `GetParameter` DSL. Used for when an implementation is used in multiple
/// different configuration.
/// </summary>
public class KeyedParameterAttribute: Attribute
{
    public ParameterKey Key { get; }

    public KeyedParameterAttribute(ParameterKey key)
    {
        Key = key;
    }
}
