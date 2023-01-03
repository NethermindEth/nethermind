// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Producers
{
    public class TriggerWithCondition : IBlockProductionTrigger
    {
        private readonly Func<BlockProductionEventArgs, bool> _checkCondition;

        public TriggerWithCondition(IBlockProductionTrigger trigger, Func<bool> checkCondition) : this(trigger, _ => checkCondition())
        {
        }

        public TriggerWithCondition(IBlockProductionTrigger trigger, Func<BlockProductionEventArgs, bool> checkCondition)
        {
            _checkCondition = checkCondition;
            trigger.TriggerBlockProduction += TriggerOnTriggerBlockProduction;
        }


        private void TriggerOnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            if (_checkCondition.Invoke(e))
            {
                TriggerBlockProduction?.Invoke(this, e);
            }
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;
    }
}
