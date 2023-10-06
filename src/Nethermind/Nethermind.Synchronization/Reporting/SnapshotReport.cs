// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.VisualBasic;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.Reporting
{
    public class SyncReportSymmary
    {
        public string CurrentStage { get; set; }
    }
}
