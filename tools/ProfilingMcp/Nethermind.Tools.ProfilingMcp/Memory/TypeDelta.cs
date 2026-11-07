// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>Change of one type between two heap snapshots.</summary>
public sealed record TypeDelta(string Name, long CountDelta, long BytesDelta, long Count, long Bytes);
