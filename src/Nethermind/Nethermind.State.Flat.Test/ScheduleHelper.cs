// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.Test;

internal static class ScheduleHelper
{
    public static CompactionSchedule CreateWithOffset(IFlatDbConfig config, long offset)
    {
        MemDb metadataDb = new();
        metadataDb.Set(MetadataDbKeys.FlatDbCompactionOffset, Rlp.Encode(offset).Bytes);
        return new CompactionSchedule(metadataDb, config, LimboLogs.Instance);
    }
}
