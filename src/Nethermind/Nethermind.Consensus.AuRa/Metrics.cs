//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System.ComponentModel;

namespace Nethermind.Consensus.AuRa
{
    public static class Metrics
    {
        [Description("Current AuRa step")]
        public static long AuRaStep { get; set; }
        
        [Description("Skipped steps")]
        public static long SkippedAuRaSteps { get; set; }
        
        [Description("Reported benign misbehaviour validators")]
        public static long ReportedBenignMisbehaviour { get; set; }
        
        [Description("Reported malicious misbehaviour validators")]
        public static long ReportedMaliciousMisbehaviour { get; set; }
        
        [Description("Validators count")]
        public static long ValidatorsCount { get; set; }

        [Description("Transactions sealed")]
        public static long SealedTransactions { get; set; }

        [Description("Commit hash transactions")]
        public static long CommitHashTransaction { get; set; }

        [Description("Reveal number transactions")]
        public static long RevealNumber { get; set; }

        [Description("Emit init change transactions")]
        public static long EmitInitiateChange { get; set; }
    }
}
