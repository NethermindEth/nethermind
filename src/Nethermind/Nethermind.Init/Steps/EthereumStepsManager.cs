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
        private readonly List<StepInfo> _allSteps;
        private readonly Dictionary<Type, StepInfo> _allStepsByBaseType;

        public EthereumStepsManager(
            IEthereumStepsLoader loader,
            INethermindApi context,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(loader);

            _api = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logManager?.GetClassLogger<EthereumStepsManager>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _allSteps = loader.LoadSteps(_api.GetType()).ToList();
            _allStepsByBaseType = _allSteps.ToDictionary(static s => s.StepBaseType, static s => s);
        }

        public async Task InitializeAll(CancellationToken cancellationToken)
        {
            CreateAndExecuteSteps(cancellationToken);

            Task current;
            do
            {
                current = await Task.WhenAny(_allRequiredSteps);
                ReviewFailedAndThrow(current);
                _allRequiredSteps.Remove(current);
            } while (_allRequiredSteps.Any(s => !s.IsCompleted));
        }

        private readonly List<Task> _allRequiredSteps = new();

        private void CreateAndExecuteSteps(CancellationToken cancellationToken)
        {
            Dictionary<Type, IStep> createdSteps = [];

            foreach (StepInfo stepInfo in _allSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IStep? step = CreateStepInstance(stepInfo);
                if (step is null)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step {stepInfo}");
                    continue;
                }
                createdSteps.Add(step.GetType(), step);
            }

            foreach (StepInfo stepInfo in _allSteps)
            {
                if (!createdSteps.ContainsKey(stepInfo.StepType))
                {
                    throw new StepDependencyException($"Could not initialize because {stepInfo} could not be created.");
                }
                IStep step = createdSteps[stepInfo.StepType];

                Task task = ExecuteStep(step, stepInfo, createdSteps, cancellationToken);
                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");

                if (step.MustInitialize)
                {
                    _allRequiredSteps.Add(task);
                }
            }
        }

        private async Task ExecuteStep(IStep step, StepInfo stepInfo, Dictionary<Type, IStep> steps, CancellationToken cancellationToken)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                IEnumerable<Task> dependencies = stepInfo.Dependencies.Select(t => steps[t].StepCompleted);
                await step.Execute(dependencies, cancellationToken);

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Step {step.GetType().Name,-24} executed in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            }
            catch (Exception exception) when (exception is not TaskCanceledException)
            {
                if (step.MustInitialize)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Step {step.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms",
                            exception);
                    throw;
                }

                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Step {step.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms {exception}");
                }
            }
            finally
            {
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

        private void ReviewFailedAndThrow(Task task)
        {
            if (task?.IsFaulted == true && task?.Exception is not null)
                ExceptionDispatchInfo.Capture(task.Exception.GetBaseException()).Throw();
        }
    }
}
