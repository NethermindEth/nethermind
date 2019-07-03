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

using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    // TODO: probably should use multiple action types
    public class ParityTraceAction
    {
        public int[] TraceAddress { get; set; }
        public string CallType { get; set; }

        public bool IncludeInTrace => !IsPrecompiled || Error != null;
        public bool IsPrecompiled { get; set; }
        public string Type { get; set; }
        public Address From { get; set; }
        public Address To { get; set; }
        public long Gas { get; set; }
        public UInt256 Value { get; set; }
        public byte[] Input { get; set; }
        public ParityTraceResult Result { get; set; } = new ParityTraceResult();
        public List<ParityTraceAction> Subtraces { get; set; } = new List<ParityTraceAction>();
        
        public Address Author { get; set; }
        public string RewardType { get; set; }
        public string Error { get; set; }
    }
}