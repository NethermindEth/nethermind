// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;

using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator
    {
        public class Cache
        {
            internal LinkedList<PersistentReport> PersistentReports { get; } = new LinkedList<PersistentReport>();

            private readonly ConcurrentLruCache<Key, bool> _lastBlockReports =
                new ConcurrentLruCache<Key, bool>(MaxQueuedReports, "ReportCache");

            internal bool AlreadyReported(ReportType reportType, Address validator, in long blockNumber)
            {
                (Address Validator, ReportType ReportType, long BlockNumber) key = (validator, reportType, blockNumber);
                bool alreadyReported = _lastBlockReports.TryGet(key, out _);
                _lastBlockReports.Set(key, true);
                return alreadyReported;
            }

            /// <summary>
            /// ValueTuples are terrible for performance as a Dictionary key
            /// </summary>
            internal readonly struct Key(Address validator, ReportType reportType, long blockNumber) : IEquatable<Key>
            {
                public readonly Address Validator = validator;
                public readonly ReportType ReportType = reportType;
                public readonly long BlockNumber = blockNumber;

                public static implicit operator Key((Address validator, ReportType reportType, long blockNumber) value) => new (value.validator, value.reportType, value.blockNumber);

                public bool Equals(Key other) => Validator == other.Validator && ReportType == other.ReportType && BlockNumber == other.BlockNumber;
                public override bool Equals(object? obj) => obj is Key other && Equals(other);
                public override int GetHashCode()
                {
                    uint hashCode = BitOperations.Crc32C((uint)Validator.GetHashCode(), (uint)ReportType.GetHashCode());
                    return (int)BitOperations.Crc32C(hashCode, (uint)BlockNumber.GetHashCode());
                }
            }
        }
    }
}
