// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public class BeneficiaryTracer : TxTracer, IBlockTracer
    {
        private Address _beneficiary = Address.Zero;

        public UInt256 BeneficiaryBalance { get; private set; }

        public override bool IsTracingState => true;

        public bool IsTracingRewards => true;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

        public void StartNewBlockTrace(Block block)
        {
            _beneficiary = block.Header.GasBeneficiary!;
        }

        public ITxTracer StartNewTxTrace(Transaction? tx) => this;

        public void EndTxTrace() { }

        public void EndBlockTrace() { }

        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            if (address == _beneficiary)
            {
                BeneficiaryBalance = after ?? UInt256.Zero;
            }
        }
    }
}
