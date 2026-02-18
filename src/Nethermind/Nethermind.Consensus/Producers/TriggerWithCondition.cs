// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Producers
{
    public class TriggerWithCondition : IBlockProductionTrigger, IDisposable
    {
        private readonly IBlockProductionTrigger _trigger;
        private readonly Func<BlockProductionEventArgs, bool> _checkCondition;

        public TriggerWithCondition(IBlockProductionTrigger trigger, Func<bool> checkCondition) : this(trigger, _ => checkCondition())
        {
        }

        public TriggerWithCondition(IBlockProductionTrigger trigger, Func<BlockProductionEventArgs, bool> checkCondition)
        {
            _trigger = trigger;
            _checkCondition = checkCondition;
            trigger.TriggerBlockProduction += TriggerOnTriggerBlockProduction;
        }


        private void TriggerOnTriggerBlockProduction(object? sender, BlockProductionEventArgs e)
        {
            if (_checkCondition(e))
            {
                TriggerBlockProduction?.Invoke(this, e);
            }
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public void Dispose()
        {
            _trigger.TriggerBlockProduction -= TriggerOnTriggerBlockProduction;
            _trigger.TryDispose();
        }
    }
}
