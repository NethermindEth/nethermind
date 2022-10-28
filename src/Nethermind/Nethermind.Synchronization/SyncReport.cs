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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualBasic;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Reports
{
    public class ProgressStage
    {
        public SyncMode SyncMode { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime FinishTime { get; set; }
        public TimeSpan Duration => FinishTime - StartTime;

        public long? Current { get; set; }
        public long? Total { get; set; }
        public double? Percent => Current / Total;
    }
    public class SyncReportSymmary
    {
        public IEnumerable<SyncMode> CurrentStage { get; set; }
        public IEnumerable<ProgressStage> Progress { get; set; }
    } 
    public static class ReportSink
    {
        public static List<SyncMode> CurrentStage { get; set; } = new();
        public static ConcurrentDictionary<SyncMode, ProgressStage> Progress { get; set; } = new();
        public static SyncReportSymmary Snapshot => new SyncReportSymmary
        {
            CurrentStage = CurrentStage,
            Progress = Progress.Values
        };
    }
}
