// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db;

public static partial class Metrics
{
    public static Dictionary<string, long> DbReads => field ??= [];
    public static Dictionary<string, long> DbWrites => field ??= [];
    public static Dictionary<string, long> DbSize => field ??= [];
    public static Dictionary<string, long> DbMemtableSize => field ??= [];
    public static Dictionary<string, long> DbBlockCacheSize => field ??= [];
    public static Dictionary<string, long> DbIndexFilterSize => field ??= [];
    // DbStats and DbCompactionStats omitted: double-valued and unread in the guest.
}
