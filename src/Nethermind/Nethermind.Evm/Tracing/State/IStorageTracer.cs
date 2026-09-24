// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.Tracing.State;

public interface IStorageTracer
{
    /// <summary>Reports a committed storage clear before slot changes from the same transaction; reverted clears are excluded.</summary>
    void ReportStorageClear(Address address) { }

    /// <summary>Reports a slot restored to its original value after a committed clear, which is not a net storage change.</summary>
    void ReportStorageRestore(in StorageCell storageCell, byte[] value) { }

    /// <summary>
    /// Controls tracing of storage
    /// </summary>
    /// <remarks>
    /// Controls
    /// - <see cref="ReportStorageChange(in StorageCell, byte[], byte[])"/>
    /// - <see cref="ReportStorageRead"/>
    /// - <see cref="ReportStorageClear"/>
    /// - <see cref="ReportStorageRestore"/>
    /// </remarks>
    bool IsTracingStorage { get; }

    /// <summary>
    /// Reports change of storage slot for key
    /// </summary>
    /// <param name="storageCell"></param>
    /// <param name="before"></param>
    /// <param name="after"></param>
    /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
    void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after);

    /// <summary>
    /// Reports storage access
    /// </summary>
    /// <param name="storageCell"></param>
    /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
    void ReportStorageRead(in StorageCell storageCell);
}
