// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

/// <summary>
/// Specifies the fixture subdirectory (relative to <c>for_amsterdam/</c>) that a test
/// class loads its cases from.  Replaces the old <c>EipWildcard</c> approach — the path
/// is explicit and maps to a real directory in the BAL archive rather than being a glob
/// filter over a shared root.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AmsterdamFixturePathAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
