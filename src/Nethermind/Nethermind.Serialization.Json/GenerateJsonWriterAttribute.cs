// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Asks the JSON writer generator for a converter that writes this exact type the way the source-generated metadata path
/// does, without its per-property dispatch.
/// </summary>
/// <remarks>
/// The generator reports a diagnostic and emits nothing for a type with a hand-written converter or a shape it cannot write
/// identically.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GenerateJsonWriterAttribute : Attribute
{
    /// <summary>
    /// Whether the writer is registered with <see cref="EthereumJsonSerializer"/>, which then reads the type through the metadata
    /// path; otherwise it is only reachable through <see cref="GeneratedJsonWriters.TryGetDispatchWriter"/>.
    /// </summary>
    /// <remarks>Set it to <see langword="false"/> for types a hand-written converter already dispatches to, so their reads stay untouched.</remarks>
    public bool RegisterWithSerializer { get; init; } = true;
}
