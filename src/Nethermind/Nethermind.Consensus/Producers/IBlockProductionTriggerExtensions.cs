// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
{
    public static class BlockProductionTriggerExtensions
    {
        public static IBlockProductionTrigger IfPoolIsNotEmpty(this IBlockProductionTrigger? trigger, ITxPool? txPool)
        {
            if (trigger is null) throw new ArgumentNullException(nameof(trigger));
            if (txPool is null) throw new ArgumentNullException(nameof(txPool));
            return trigger.ButOnlyWhen(() => txPool.GetPendingTransactionsCount() > 0);
        }

        public static IBlockProductionTrigger ButOnlyWhen(this IBlockProductionTrigger? trigger, Func<bool> condition)
        {
            if (trigger is null) throw new ArgumentNullException(nameof(trigger));
            return new TriggerWithCondition(trigger, condition);
        }

        public static IBlockProductionTrigger Or(this IBlockProductionTrigger? trigger, IBlockProductionTrigger? alternative)
        {
            if (trigger is null) throw new ArgumentNullException(nameof(trigger));
            if (alternative is null) throw new ArgumentNullException(nameof(alternative));

            if (trigger is CompositeBlockProductionTrigger composite1)
            {
                composite1.Add(alternative);
                return composite1;
            }
            else if (alternative is CompositeBlockProductionTrigger composite2)
            {
                composite2.Add(trigger);
                return composite2;
            }
            else
            {
                return new CompositeBlockProductionTrigger(trigger, alternative);
            }
        }
    }
}
