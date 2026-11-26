// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Serialization.Json;

/// <summary>Writes a 32-byte storage key as 32 bytes and a shorter one as a quantity.</summary>
public sealed class ProofStorageKeyConverter : ByteArrayConverter
{
    public override void Write(Utf8JsonWriter writer, byte[] bytes, JsonSerializerOptions options) =>
        Convert(writer, bytes, skipLeadingZeros: bytes.Length < 32);
}
