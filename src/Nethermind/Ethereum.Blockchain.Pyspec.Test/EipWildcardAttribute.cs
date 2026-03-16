// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Ethereum.Blockchain.Pyspec.Test;

[AttributeUsage(AttributeTargets.Class)]
public sealed class EipWildcardAttribute(string wildcard) : Attribute
{
    public string Wildcard { get; } = wildcard;
}
