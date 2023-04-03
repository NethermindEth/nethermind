// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.State
{
    public interface IStorageTracer
    {
        /// <summary>
        /// Controls tracing of storage
        /// </summary>
        /// <remarks>
        /// Controls
        /// - <see cref="ReportStorageChange"/>
        /// - <see cref="ReportStorageRead"/>
        /// </remarks>
        bool IsTracingStorage { get; }

        /// <summary>
        /// Reports change of storage slot for key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value);

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
}
