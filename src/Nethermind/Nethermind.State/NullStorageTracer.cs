// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State
{
    public class NullStorageTracer : IStorageTracer
    {
        private NullStorageTracer() { }

        public static IStorageTracer Instance { get; } = new NullStorageTracer();

        private const string ErrorMessage = "Null tracer should never receive any calls.";

        public bool IsTracingStorage => false;
        public void ReportStorageChange(in UInt256 key, in UInt256 value)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportStorageChange(in StorageCell storageCell, in UInt256 before, in UInt256 after)
            => throw new InvalidOperationException(ErrorMessage);

        public void ReportStorageRead(in StorageCell storageCell)
            => throw new InvalidOperationException(ErrorMessage);
    }
}
