// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>Reads committed EIP-8347 migration progress and optional hash-addressed shadow roots.</summary>
public interface IMigrationTelemetry
{
    /// <summary>Returns a coherent snapshot of committed migration cursors.</summary>
    MigrationProgressForRpc GetProgress();

    /// <summary>Returns the retained noncanonical commitment, or null when unavailable.</summary>
    Hash256? GetShadowRoot(Hash256 blockHash);
}

/// <summary>Migration lifecycle and its independently advancing directions.</summary>
public sealed record MigrationProgressForRpc(
    string Phase,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MigrationDirectionForRpc? Binary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MigrationDirectionForRpc? Merkle)
{
    /// <summary>Progress when migration is not enabled.</summary>
    public static MigrationProgressForRpc Inactive { get; } = new("inactive", null, null);
}

/// <summary>A committed migration direction; cursor numbers use JSON integers, not RPC quantities.</summary>
public sealed record MigrationDirectionForRpc(
    string Phase,
    [property: JsonConverter(typeof(ULongRawJsonConverter))] ulong Cursor,
    Hash256 CursorHash,
    Hash256 ShadowRoot,
    string Error);
