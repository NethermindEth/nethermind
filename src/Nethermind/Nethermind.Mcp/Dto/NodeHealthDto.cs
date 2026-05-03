// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record HealthCheckDto(string Name, string Status, string? Message, object? Value);

public sealed record NodeHealthDto(
    string OverallStatus,
    HealthCheckDto[] Checks,
    long UptimeSeconds,
    long ProcessMemoryMb,
    int GcGen0Collections,
    int GcGen1Collections,
    int GcGen2Collections,
    long? DiskFreeGb,
    long? DiskUsedGb);
