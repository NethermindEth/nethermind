// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Asks the JSON writer generator for a converter that writes this exact type the way the source-generated metadata path
/// does, without its per-property dispatch.
/// </summary>
/// <remarks>
/// The generated converter registers itself with <see cref="EthereumJsonSerializer"/> and reads through the metadata path.
/// The generator reports a diagnostic and emits nothing for a type with a hand-written converter or a shape it cannot write
/// identically.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GenerateJsonWriterAttribute : Attribute;
