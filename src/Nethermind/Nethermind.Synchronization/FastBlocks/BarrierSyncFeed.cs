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

public abstract class BarrierSyncFeed<T> : ActivatedSyncFeed<T>
{
    protected abstract ulong? LowestInsertedNumber { get; }
    protected abstract int BarrierWhenStartedMetadataDbKey { get; }
    protected abstract long SyncConfigBarrierCalc { get; }
    protected abstract Func<bool> HasPivot { get; }

    protected readonly ISpecProvider _specProvider;
    protected readonly ILogger _logger;
    protected ulong _barrier;
    protected ulong _pivotNumber;
    protected ulong? _barrierWhenStarted;

    protected readonly IDb _metadataDb;

    public BarrierSyncFeed(IDb metadataDb, ISpecProvider specProvider, ILogger logger)
    {
        _metadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logger;
    }

    public void InitializeMetadataDb()
    {
        if (!HasPivot())
        {
            _barrierWhenStarted = checked((ulong)SyncConfigBarrierCalc);
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
        else if (_metadataDb.KeyExists(BarrierWhenStartedMetadataDbKey))
        {
            _barrierWhenStarted = _metadataDb.Get(BarrierWhenStartedMetadataDbKey).ToULongFromBigEndianByteArrayWithoutLeadingZeros();
        }
        else
        {
            _barrierWhenStarted = _barrier;
            _metadataDb.Set(BarrierWhenStartedMetadataDbKey, _barrierWhenStarted.Value.ToBigEndianByteArrayWithoutLeadingZeros());
        }
    }
}
