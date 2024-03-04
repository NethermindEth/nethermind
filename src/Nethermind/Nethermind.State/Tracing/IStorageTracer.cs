// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.State.Tracing
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
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageChange(in ReadOnlySpan<byte> key, EvmWord value)
            => ReportStorageChange(key, value.AsSpan());

        /// <summary>
        /// Reports change of storage slot for key
        /// </summary>
        /// <param name="storageCell"></param>
        /// <param name="before"></param>
        /// <param name="after"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after);

        /// <summary>
        /// Reports change of storage slot for key
        /// </summary>
        /// <param name="storageCell"></param>
        /// <param name="before"></param>
        /// <param name="after"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageChange(in StorageCell storageCell, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
            => ReportStorageChange(storageCell, before.ToArray(), after.ToArray());

        /// <summary>
        /// Reports storage access
        /// </summary>
        /// <param name="storageCell"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageRead(in StorageCell storageCell);
    }
}
