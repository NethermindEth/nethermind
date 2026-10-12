// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>Which implementation runs the flat-state DB.</summary>
public enum FlatDbBackend
{
    /// <summary>The C# implementation.</summary>
    CSharp,

    /// <summary>The Rust persistence layer under the C# snapshot manager.</summary>
    RustPersistence,

    /// <summary>The Rust snapshot manager and persistence layer.</summary>
    Rust,
}
