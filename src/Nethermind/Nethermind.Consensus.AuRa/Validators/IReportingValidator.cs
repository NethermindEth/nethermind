﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Validators
{
    public interface IReportingValidator
    {
        void ReportMalicious(Address validator, long blockNumber, byte[] proof, MaliciousCause cause);
        void ReportBenign(Address validator, long blockNumber, BenignCause cause);
        void TryReportSkipped(BlockHeader header, BlockHeader parent);
        
        public enum BenignCause
        {
            FutureBlock,
            IncorrectProposer,
            SkippedStep
        }
        
        public enum MaliciousCause
        {
            DuplicateStep,
            SiblingBlocksInSameStep
        }
    }
}