// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(System.Collections.Generic.IEnumerable<string>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(Nethermind.Core.TxReceipt))]
internal partial class SerializationJsonContext : JsonSerializerContext;
