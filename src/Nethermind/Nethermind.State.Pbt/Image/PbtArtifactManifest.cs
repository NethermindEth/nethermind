// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.State.Pbt.Image;

/// <summary>Decodes advisory provenance; callers still authenticate against their own chain anchor.</summary>
internal static class PbtArtifactManifest
{
    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    public static PbtArtifactIdentity Read(Stream manifest)
    {
        const int maximumBytes = 16 * 1024;
        byte[] bytes = new byte[maximumBytes + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = manifest.Read(bytes.AsSpan(length));
            if (read == 0) break;
            length += read;
        }
        if (length > maximumBytes) throw new InvalidDataException("PBT manifest exceeds the local metadata size limit.");
        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported PBT manifest version.");
        HashSet<string> fields = [];
        foreach (JsonProperty property in root.EnumerateObject())
            if (!fields.Add(property.Name)) throw new InvalidDataException("Duplicate PBT manifest property.");
        string Text(string name) => root.GetProperty(name).GetString() is { Length: > 0 } text
            ? text : throw new InvalidDataException($"Missing PBT manifest {name}.");
        string Hash(string name)
        {
            string value = Text(name);
            if (value.Length != 66 || !value.StartsWith("0x", StringComparison.Ordinal) || !IsHex(value.AsSpan(2)))
                throw new InvalidDataException($"Invalid PBT manifest {name}.");
            return value;
        }
        _ = Hash("pbtRoot");
        _ = Hash("snapshotDigest");
        _ = Hash("preimageDigest");
        return new(Text("chainId"), Hash("genesisHash"), Hash("anchorHash"), root.GetProperty("anchorNumber").GetUInt64(),
            Hash("anchorMptRoot"), Text("formatRevision"), Text("producerRevision"), Text("sourceKind"));
    }
}
