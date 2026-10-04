// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

[JsonSerializable(typeof(double[]))]
internal partial class SerializationJsonContext : JsonSerializerContext;
