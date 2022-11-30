// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Baseline.Config
{
    public class BaselineConfig : IBaselineConfig
    {
        public bool Enabled { get; set; }

        public bool BaselineTreeDbCacheIndexAndFilterBlocks { get; set; } = false;
        public ulong BaselineTreeDbBlockCacheSize { get; set; } = (ulong)1.KiB();
        public ulong BaselineTreeDbWriteBufferSize { get; set; } = (ulong)1.KiB();
        public uint BaselineTreeDbWriteBufferNumber { get; set; } = 4;

        public bool BaselineTreeMetadataDbCacheIndexAndFilterBlocks { get; set; } = false;
        public ulong BaselineTreeMetadataDbBlockCacheSize { get; set; } = (ulong)1.KiB();
        public ulong BaselineTreeMetadataDbWriteBufferSize { get; set; } = (ulong)1.KiB();
        public uint BaselineTreeMetadataDbWriteBufferNumber { get; set; } = 4;
    }
}
