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
using System.Linq;

namespace Nethermind.Abi
{
    public class AbiFunctionDescription : AbiBaseDescription<AbiParameter>
    {
        private AbiSignature? _returnSignature;
        public AbiParameter[] Outputs { get; set; } = Array.Empty<AbiParameter>();

        public StateMutability StateMutability { get; set; } = StateMutability.View;

        public bool Payable
        {
            get => StateMutability == StateMutability.Payable;
            set => StateMutability = value ? StateMutability.Payable : StateMutability;
        }

        public bool Constant
        {
            get => StateMutability == StateMutability.Pure || StateMutability == StateMutability.View;
            set
            {
                if (Constant != value)
                {
                    StateMutability = value 
                        ? StateMutability.View 
                        : Payable ? StateMutability.Payable : StateMutability.NonPayable;
                }
            }
        }

        public AbiEncodingInfo GetReturnInfo() => new(AbiEncodingStyle.None, _returnSignature ??= new AbiSignature(Name, Outputs.Select(i => i.Type).ToArray()));
    }
}
