// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Consensus.Producers
{
    public class CompositeBlockProductionTrigger : IBlockProductionTrigger, IDisposable
    {
        private readonly IList<IBlockProductionTrigger> _triggers;

        public CompositeBlockProductionTrigger(params IBlockProductionTrigger[] triggers)
        {
            _triggers = triggers.ToList();
            for (int index = 0; index < _triggers.Count; index++)
            {
                HookTrigger(_triggers[index]);
            }
        }

        internal void Add(IBlockProductionTrigger trigger)
        {
            _triggers.Add(trigger);
            HookTrigger(trigger);
        }

        private void HookTrigger(IBlockProductionTrigger trigger) =>
            trigger.TriggerBlockProduction += OnInnerTriggerBlockProduction;

        private void OnInnerTriggerBlockProduction(object? sender, BlockProductionEventArgs e) =>
            TriggerBlockProduction?.Invoke(sender, e);

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public void Dispose()
        {
            for (int index = 0; index < _triggers.Count; index++)
            {
                var trigger = _triggers[index];
                trigger.TriggerBlockProduction -= OnInnerTriggerBlockProduction;
                if (trigger is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}
