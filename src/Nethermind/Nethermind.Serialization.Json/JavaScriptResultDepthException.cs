// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Serialization.Json;

/// <summary>A script value nests deeper than the serializer's maximum depth, or holds itself.</summary>
public sealed class JavaScriptResultDepthException(int maxDepth)
    : JsonException($"JavaScript tracer result exceeds the maximum depth of {maxDepth} (possibly cyclic)");
