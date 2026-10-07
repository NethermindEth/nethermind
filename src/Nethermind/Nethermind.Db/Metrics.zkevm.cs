// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db;

public static partial class Metrics
{
    public static Dictionary<string, long> DbReads { get; } = [];
    public static Dictionary<string, long> DbWrites { get; } = [];
    public static Dictionary<string, long> DbSize { get; } = [];
    public static Dictionary<string, long> DbMemtableSize { get; } = [];
    public static Dictionary<string, long> DbBlockCacheSize { get; } = [];
    public static Dictionary<string, long> DbIndexFilterSize { get; } = [];
    // DbStats and DbCompactionStats omitted: double-valued and unread in the guest.
}
