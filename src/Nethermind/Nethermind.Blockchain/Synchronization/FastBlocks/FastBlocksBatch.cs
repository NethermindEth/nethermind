/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;

namespace Nethermind.Blockchain.Synchronization.FastBlocks
{
    public class FastBlocksBatch
    {
        private Stopwatch _stopwatch = new Stopwatch();
        private long? _scheduledLastTime;
        private long? _requestSentTime;
        private long? _validationStartTime;
        private long? _waitingStartTime;
        private long? _handlingStartTime;
        private long? _handlingEndTime;
        
        public bool Prioritized { get; set; }

        public bool IsResponseEmpty => Bodies?.Response == null && Headers?.Response == null && Receipts?.Response == null;
        public FastBlocksBatchType BatchType => Headers != null ? FastBlocksBatchType.Headers : Bodies == null ? FastBlocksBatchType.Receipts : FastBlocksBatchType.Bodies;
        public ReceiptsSyncBatch Receipts { get; set; }
        public HeadersSyncBatch Headers { get; set; }
        public BodiesSyncBatch Bodies { get; set; }
        public SyncPeerAllocation Allocation { get; set; }
        public PeerInfo OriginalDataSource { get; set; }

        public FastBlocksBatch()
        {
            _stopwatch.Start();
            _scheduledLastTime = _stopwatch.ElapsedMilliseconds;
        }
        
        public void MarkRetry()
        {
            Retries++;
            _scheduledLastTime = _stopwatch.ElapsedMilliseconds;
            _validationStartTime = null;
            _requestSentTime = null;
            _handlingStartTime = null;
            _handlingEndTime = null;
        }
        
        public void MarkSent()
        {
            _requestSentTime = _stopwatch.ElapsedMilliseconds;
            
        }
        public void MarkValidation()
        {
            _validationStartTime = _stopwatch.ElapsedMilliseconds;
        }
        
        public void MarkWaiting()
        {
            _waitingStartTime = _stopwatch.ElapsedMilliseconds;
        }
        
        public void MarkHandlingStart()
        {
            _handlingStartTime = _stopwatch.ElapsedMilliseconds;
            
            if (_validationStartTime == null)
            {
                _validationStartTime = _handlingStartTime;
            }
        }
        
        public void MarkHandlingEnd()
        {
            _handlingEndTime = _stopwatch.ElapsedMilliseconds;
        }
        
        private int Retries { get; set; }
        public double? AgeInMs => _stopwatch.ElapsedMilliseconds;
        public double? SchedulingTime => (_requestSentTime ?? _stopwatch.ElapsedMilliseconds) - (_scheduledLastTime ?? _stopwatch.ElapsedMilliseconds);
        public double? RequestTime => (_validationStartTime ?? _stopwatch.ElapsedMilliseconds) - (_requestSentTime ?? _stopwatch.ElapsedMilliseconds);
        public double? ValidationTime => (_waitingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_validationStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? WaitingTime => (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds) - (_waitingStartTime ?? _handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public double? HandlingTime => (_handlingEndTime ?? _stopwatch.ElapsedMilliseconds) - (_handlingStartTime ?? _stopwatch.ElapsedMilliseconds);
        public long? MinNumber { get; set; }
        
        public override string ToString()
        {
            string startBlock = Headers?.StartNumber.ToString();
            string endBlock = (Headers?.StartNumber != null ? Headers.StartNumber + (Headers.RequestSize - 1) : (Headers?.RequestSize ?? 0) - 1).ToString();
            string details = string.Empty;
            switch (BatchType)
            {
                case FastBlocksBatchType.None:
                    break;
                case FastBlocksBatchType.Headers:
                    details = $"[{startBlock}, {endBlock}]({Headers?.RequestSize ?? Bodies?.Request.Length})";
                    break;
                case FastBlocksBatchType.Bodies:
                    details = $"({Bodies.Request.Length})";
                    break;
                case FastBlocksBatchType.Receipts:
                    details = $"[{Receipts.Blocks[Receipts.Blocks.Length - 1].Number},{Receipts.Blocks[0].Number}]({Receipts.Request.Length})";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            string priority = Prioritized ? "HIGH" : "LOW";
            return $"{BatchType} {details} [{priority}] [times: S:{SchedulingTime:F0}ms|R:{RequestTime:F0}ms|V:{ValidationTime:F0}ms|W:{WaitingTime:F0}ms|H:{HandlingTime:F0}ms|A:{AgeInMs:F0}ms, retries {Retries}] min#: {MinNumber} {Allocation?.Current ?? OriginalDataSource}";
        }
    }
}