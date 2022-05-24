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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsManager
    {
        private ILogger _logger;

        private AutoResetEvent _autoResetEvent = new AutoResetEvent(true);
        private readonly INethermindApi _api;
        private readonly List<StepInfo> _allSteps;
        private readonly Dictionary<Type, StepInfo> _allStepsByBaseType;

        public EthereumStepsManager(
            IEthereumStepsLoader loader,
            INethermindApi context,
            ILogManager logManager)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _api = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logManager?.GetClassLogger<EthereumStepsManager>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _allSteps = loader.LoadSteps(_api.GetType()).ToList();
            _allStepsByBaseType = _allSteps.ToDictionary(s => s.StepBaseType, s => s);
        }

        private async Task ReviewDependencies(CancellationToken cancellationToken)
        {
            bool changedAnything;
            do
            {
                foreach (StepInfo stepInfo in _allSteps)
                {
                    _logger.Debug($"{stepInfo} is {stepInfo.Stage}");
                }
                
                await _autoResetEvent.WaitOneAsync(cancellationToken);
                
                if (_logger.IsDebug) _logger.Debug("Reviewing steps manager dependencies");
                
                changedAnything = false;
                foreach (StepInfo stepInfo in _allSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (stepInfo.Stage == StepInitializationStage.WaitingForDependencies)
                    {
                        bool allDependenciesFinished = true;
                        foreach (Type dependency in stepInfo.Dependencies)
                        {
                            StepInfo dependencyInfo = _allStepsByBaseType[dependency];
                            if (dependencyInfo.Stage != StepInitializationStage.Complete)
                            {
                                if (_logger.IsDebug) _logger.Debug($"{stepInfo} is waiting for {dependencyInfo}");
                                allDependenciesFinished = false;
                                break;
                            }
                        }

                        if (allDependenciesFinished)
                        {
                            stepInfo.Stage = StepInitializationStage.WaitingForExecution;
                            changedAnything = true;
                            if (_logger.IsDebug) _logger.Debug($"{stepInfo} stage changed to {stepInfo.Stage}");
                            _autoResetEvent.Set();
                        }
                    }
                }
            } while (changedAnything);
        }

        public async Task InitializeAll(CancellationToken cancellationToken)
        {
            while (_allSteps.Any(s => s.Stage != StepInitializationStage.Complete))
            {
                cancellationToken.ThrowIfCancellationRequested();

                RunOneRoundOfInitialization(cancellationToken);
                await ReviewDependencies(cancellationToken);
            }

            await Task.WhenAll(_allPending);
        }

        private readonly ConcurrentQueue<Task> _allPending = new();

        private void RunOneRoundOfInitialization(CancellationToken cancellationToken)
        {
            int startedThisRound = 0;
            foreach (StepInfo stepInfo in _allSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (stepInfo.Stage != StepInitializationStage.WaitingForExecution)
                {
                    continue;
                }

                IStep? step = CreateStepInstance(stepInfo);
                if (step == null)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step {stepInfo}");
                    continue;
                }

                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");

                stepInfo.Stage = StepInitializationStage.Executing;

                startedThisRound++;
                Task task = RunPluginBeforeStep(step)
                    .ContinueWith(_ => RunStep(step, stepInfo, cancellationToken))
                    .ContinueWith(_ => RunPluginAfterStep(step));

                if (step.MustInitialize)
                {
                    _allPending.Enqueue(task);
                }
                else
                {
                    stepInfo.Stage = StepInitializationStage.Complete;
                }
            }

            if (startedThisRound == 0 && _allPending.All(t => t.IsCompleted))
            {
                Interlocked.Increment(ref _foreverLoop);
                if (_foreverLoop > 100)
                {
                    if (_logger.IsWarn) _logger.Warn($"Didn't start any initialization steps during initialization round and all previous steps are already completed.");
                }
            }
        }

        private IStep? CreateStepInstance(StepInfo stepInfo)
        {
            IStep? step = null;
            try
            {
                step = Activator.CreateInstance(stepInfo.StepType, _api) as IStep;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to create instance of Ethereum runner step {stepInfo}", e);
            }

            return step;
        }

        private async Task RunPluginBeforeStep(IStep step)
        {
            foreach (var plugin in _api.Plugins)
            {
                try
                {
                    await plugin.OnBeforeStep(step.Name);
                    if (_logger.IsDebug)
                        _logger.Debug($"Plugin {plugin.Name} executed BeforeStep('{step.Name}')");
                }
                catch (Exception e)
                {
                    if (_logger.IsError)
                        _logger.Error($"Plugin {plugin.Name} failed to execute BeforeStep('{step.Name}')", e);
                }
            }
        }

        private async Task RunPluginAfterStep(IStep step)
        {
            foreach (var plugin in _api.Plugins)
            {
                try
                {
                    await plugin.OnAfterStep(step.Name);
                    if (_logger.IsDebug)
                        _logger.Debug($"Plugin {plugin.Name} executed AfterStep('{step.Name}')");
                }
                catch (Exception e)
                {
                    if (_logger.IsError)
                        _logger.Error($"Plugin {plugin.Name} failed to execute AfterStep('{step.Name}')", e);
                }
            }
        }

        private async Task RunStep(IStep step, StepInfo stepInfo, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                await step.Execute(cancellationToken);
                stopwatch.Stop();

                if (_logger.IsDebug) _logger.Debug(
                    $"Step {step.Name.PadRight(24)} executed in {stopwatch.ElapsedMilliseconds}ms");
            }
            catch (Exception e)
            {
                stopwatch.Stop();
                if (step.MustInitialize)
                {
                    if (_logger.IsError) _logger.Error(
                        $"Step {step.Name.PadRight(24)} failed after {stopwatch.ElapsedMilliseconds}ms", e);
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug(
                        $"Step {step.Name.PadRight(24)} executed in {stopwatch.ElapsedMilliseconds}ms");
                }
            }

            stepInfo.Stage = StepInitializationStage.Complete;
            _autoResetEvent.Set();

            if (_logger.IsInfo) _logger.Info($"{step.Name.PadRight(24)} complete");
        }

        private int _foreverLoop;
    }
}
