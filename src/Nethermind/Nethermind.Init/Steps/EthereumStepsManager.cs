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
            if (loader is null)
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
                ReviewFailedAndThrow();
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

            if (startedThisRound == 0 && _allPending.All(t => t.IsCompleted))
            {
                Interlocked.Increment(ref _foreverLoop);
                if (_foreverLoop > 100)
                {
                    if (_logger.IsWarn) _logger.Warn($"Didn't start any initialization steps during initialization round and all previous steps are already completed.");
                }
            }
        }

        private async Task ExecuteStep(IStep step, StepInfo stepInfo, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                await step.Execute(cancellationToken);

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Step {step.GetType().Name.PadRight(24)} executed in {stopwatch.ElapsedMilliseconds}ms");

                stepInfo.Stage = StepInitializationStage.Complete;
            }
            catch (Exception exception)
            {
                if (step.MustInitialize)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Step {step.GetType().Name.PadRight(24)} failed after {stopwatch.ElapsedMilliseconds}ms",
                            exception);

                    stepInfo.Stage = StepInitializationStage.Failed;
                    throw;
                }

                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Step {step.GetType().Name.PadRight(24)} failed after {stopwatch.ElapsedMilliseconds}ms {exception}");
                }
                stepInfo.Stage = StepInitializationStage.Complete;
            }
            finally
            {
                stopwatch.Stop();
                _autoResetEvent.Set();

                if (_logger.IsDebug) _logger.Debug($"{step.GetType().Name.PadRight(24)} complete");
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
            Task? anyFaulted = _allPending.FirstOrDefault(t => t.IsFaulted);
            if (anyFaulted?.IsFaulted == true && anyFaulted?.Exception is not null)
                ExceptionDispatchInfo.Capture(anyFaulted.Exception.GetBaseException()).Throw();
        }
    }
}
