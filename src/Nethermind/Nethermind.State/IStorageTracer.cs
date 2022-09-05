//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
        void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after);

        /// <summary>
        /// Reports storage access
        /// </summary>
        /// <param name="storageCell"></param>
        /// <remarks>Depends on <see cref="IsTracingStorage"/></remarks>
        void ReportStorageRead(StorageCell storageCell);
    }
}
