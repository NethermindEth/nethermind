// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.FastBlocks;

public abstract class BarrierSyncFeed<T>(IDb metadataDb, ISpecProvider specProvider, ILogger logger) : ActivatedSyncFeed<T>
{
    protected abstract ulong? LowestInsertedNumber { get; }
    protected abstract int BarrierWhenStartedMetadataDbKey { get; }
    protected abstract ulong SyncConfigBarrierCalc { get; }
    protected abstract Func<bool> HasPivot { get; }

    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly ILogger _logger = logger;
    protected ulong _barrier;
    protected ulong _pivotNumber;
    protected ulong? _barrierWhenStarted;

    protected readonly IDb _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));

    public void InitializeMetadataDb()
    {
        if (!HasPivot())
        {
            _barrierWhenStarted = SyncConfigBarrierCalc;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
        else if (_metadataDb.KeyExists(BarrierWhenStartedMetadataDbKey))
        {
            _barrierWhenStarted = _metadataDb.GetULongFromBigEndianByteArrayWithoutLeadingZeros((ulong)BarrierWhenStartedMetadataDbKey);
        }
        else
        {
            _barrierWhenStarted = _barrier;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
    }
}
