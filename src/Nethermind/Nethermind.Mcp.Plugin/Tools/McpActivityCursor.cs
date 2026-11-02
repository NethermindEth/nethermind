// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Pins an activity window and each event filter's next unread position.</summary>
internal sealed class McpActivityCursor
{
    public ulong From { get; set; }
    public ulong RequestedFrom { get; set; }
    public ulong To { get; set; }
    public ulong? ReceiptFloor { get; set; }
    public bool HadMatches { get; set; }
    public ulong ScanFrom { get; set; }
    public ulong ScanTo { get; set; }
    public ulong Window { get; set; }
    public ulong? CheckedThrough { get; set; }
    public byte[] FilterHash { get; set; } = [];
    public ActivityPosition[] Positions { get; set; } = [];

    public McpActivityCursor Snapshot(ActivityPosition[] positions)
    {
        McpActivityCursor copy = (McpActivityCursor)MemberwiseClone();
        copy.Positions = positions;
        return copy;
    }

    public string Encode(Hash256 anchor)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(this);
        McpLogCursor signature = new(To, 0, To, SHA256.HashData(data), anchor.BytesToArray());
        return Convert.ToBase64String(data) + "." + signature.Encode();
    }

    public static bool TryDecode(string text, out McpActivityCursor? cursor, out McpLogCursor signature)
    {
        cursor = null;
        signature = default;
        if (text.Length > 4096) return false;
        int separator = text.IndexOf('.');
        if (separator <= 0 || !McpLogCursor.TryDecode(text[(separator + 1)..], out _, out signature, "address_activity")) return false;
        try
        {
            byte[] data = Convert.FromBase64String(text[..separator]);
            if (!signature.Matches(SHA256.HashData(data))) return false;
            cursor = JsonSerializer.Deserialize<McpActivityCursor>(data);
            return cursor is not null && cursor.Positions.Length is >= 4 and <= 5 && cursor.To == signature.Block;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }
}

internal readonly record struct ActivityPosition(ulong Block, long Index, ulong Span, bool Exact = false, bool Done = false, bool Ready = false);
