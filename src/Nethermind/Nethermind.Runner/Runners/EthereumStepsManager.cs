//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;

namespace Nethermind.Runner.Runners
{
    public class EthereumStepsManager
    {
        private readonly EthereumRunnerContext _context;
        private ILogger _logger;

        public EthereumStepsManager(EthereumRunnerContext context)
        {
            _context = context;
            _logger = _context.LogManager.GetClassLogger<EthereumStepsManager>();
        }

        public void DiscoverAll()
        {
            Type[] types = GetType().Assembly.GetTypes()
                .Where(t => !t.IsInterface && typeof(IStep).IsAssignableFrom(t)).ToArray();

            foreach (Type type in types)
            {
                if(_logger.IsInfo) _logger.Info($"Discovered Ethereum step: {type.Name}");
                _discoveredSteps.Add(type);
            }
        }

        private List<Type> _discoveredSteps = new List<Type>();

        private List<Task> _allTasks = new List<Task>();

        public async Task InitializeAll()
        {
            foreach (Type discoveredStep in _discoveredSteps)
            {
                IStep step;
                try
                {
                    step = Activator.CreateInstance(discoveredStep, _context) as IStep;
                }
                catch (Exception e)
                {
                    if(_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step of type {discoveredStep}", e);
                    continue;
                }
                
                if (step == null)
                {
                    if(_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step of type {discoveredStep}");
                    continue;
                }

                if(_logger.IsInfo) _logger.Info($"Executing step: {step.GetType().Name}");
                
                ISubsystemStateAware subsystemStateAware = step as ISubsystemStateAware;
                if (subsystemStateAware != null)
                {
                    subsystemStateAware.SubsystemStateChanged += SubsystemStateAwareOnSubsystemStateChanged;
                }
                
                Stopwatch stopwatch = Stopwatch.StartNew();
                await step.Execute();
                stopwatch.Stop();
                if(_logger.IsInfo) _logger.Info($"Step {step.GetType().Name} executed in {stopwatch.ElapsedMilliseconds}ms");
            }
        }

        private void SubsystemStateAwareOnSubsystemStateChanged(object sender, SubsystemStateEventArgs e)
        {
            ISubsystemStateAware subsystemStateAware = sender as ISubsystemStateAware;
            if (subsystemStateAware is null)
            {
                if(_logger.IsError) _logger.Error($"Received a subsystem state event from an unexpected type of {sender.GetType()}");
                return;
            }
            
            if(_logger.IsInfo) _logger.Info($"{subsystemStateAware.MonitoredSubsystem} state changed to {e.State}");
        }
    }
}