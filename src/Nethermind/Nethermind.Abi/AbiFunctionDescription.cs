// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
