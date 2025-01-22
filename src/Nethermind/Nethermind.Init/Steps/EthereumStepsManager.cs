// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsManager
    {
        private readonly ILogger _logger;

        private readonly AutoResetEvent _autoResetEvent = new AutoResetEvent(true);
        private readonly INethermindApi _api;
        private readonly List<StepState> _allSteps;
        private readonly Dictionary<Type, StepState> _allStepsByBaseType;

        public EthereumStepsManager(
            IEthereumStepsLoader loader,
            INethermindApi context,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(loader);

            _api = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logManager?.GetClassLogger<EthereumStepsManager>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _allSteps = loader.ResolveStepsImplementations(_api.GetType()).Select((s) => new StepState(s)).ToList();
            _allStepsByBaseType = _allSteps.ToDictionary(static s => s.StepBaseType, static s => s);
        }

        private async Task ReviewDependencies(CancellationToken cancellationToken)
        {
            bool changedAnything;
            do
            {
                foreach (StepState stepInfo in _allSteps)
                {
                    _logger.Debug($"{stepInfo} is {stepInfo.Stage}");
                }

                await _autoResetEvent.WaitOneAsync(cancellationToken);

                if (_logger.IsDebug) _logger.Debug("Reviewing steps manager dependencies");

                changedAnything = false;
                foreach (StepState stepInfo in _allSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (stepInfo.Stage == StepInitializationStage.WaitingForDependencies)
                    {
                        bool allDependenciesFinished = true;
                        foreach (Type dependency in stepInfo.Dependencies)
                        {
                            StepState dependencyInfo = _allStepsByBaseType[dependency];
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
            while (_allSteps.Any(static s => s.Stage != StepInitializationStage.Complete))
            {
                cancellationToken.ThrowIfCancellationRequested();

                RunOneRoundOfInitialization(cancellationToken);
                await ReviewDependencies(cancellationToken);
                ReviewFailedAndThrow();
            }

            await Task.WhenAll(_allPending);
        }

        private readonly ConcurrentQueue<Task> _allPending = new();

        private void RunOneRoundOfInitialization(CancellationToken cancellationToken)
        {
            int startedThisRound = 0;
            foreach (StepState stepInfo in _allSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stepInfo.Stage != StepInitializationStage.WaitingForExecution)
                {
                    continue;
                }

                IStep? step = CreateStepInstance(stepInfo.StepInfo);
                if (step is null)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step {stepInfo}");
                    continue;
                }

                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");

                stepInfo.Stage = StepInitializationStage.Executing;
                startedThisRound++;
                Task task = ExecuteStep(step, stepInfo, cancellationToken);

                if (step.MustInitialize)
                {
                    _allPending.Enqueue(task);
                }
                else
                {
                    stepInfo.Stage = StepInitializationStage.Complete;
                }
            }

            if (startedThisRound == 0 && _allPending.All(static t => t.IsCompleted))
            {
                Interlocked.Increment(ref _foreverLoop);
                if (_foreverLoop > 100)
                {
                    if (_logger.IsWarn) _logger.Warn($"Didn't start any initialization steps during initialization round and all previous steps are already completed.");
                }
            }
        }

        private async Task ExecuteStep(IStep step, StepState stepState, CancellationToken cancellationToken)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                await step.Execute(cancellationToken);

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Step {step.GetType().Name,-24} executed in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");

                stepState.Stage = StepInitializationStage.Complete;
            }
            catch (Exception exception)
            {
                if (step.MustInitialize)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Step {step.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms",
                            exception);

                    stepState.Stage = StepInitializationStage.Failed;
                    throw;
                }

                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Step {step.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms {exception}");
                }
                stepState.Stage = StepInitializationStage.Complete;
            }
            finally
            {
                _autoResetEvent.Set();

                if (_logger.IsDebug) _logger.Debug($"{step.GetType().Name,-24} complete");
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

        private int _foreverLoop;

        private void ReviewFailedAndThrow()
        {
            Task? anyFaulted = _allPending.FirstOrDefault(static t => t.IsFaulted);
            if (anyFaulted?.IsFaulted == true && anyFaulted?.Exception is not null)
                ExceptionDispatchInfo.Capture(anyFaulted.Exception.GetBaseException()).Throw();
        }

        private class StepState(StepInfo stepInfo)
        {
            public StepInitializationStage Stage { get; set; }
            public Type[] Dependencies => stepInfo.Dependencies;
            public Type StepBaseType => stepInfo.StepBaseType;
            public StepInfo StepInfo => stepInfo;

            public override string ToString()
            {
                return $"{stepInfo.StepType.Name} : {stepInfo.StepBaseType.Name} ({Stage})";
            }
        }
    }
}
