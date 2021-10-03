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
// 

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Blockchain.Producers
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
