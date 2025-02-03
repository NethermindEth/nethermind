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

        private readonly INethermindApi _api;
        private readonly List<StepInfo> _allSteps;

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
        }

        public async Task InitializeAll(CancellationToken cancellationToken)
        {
            List<Task> allRequiredSteps = CreateAndExecuteSteps(cancellationToken);
            if (allRequiredSteps.Count == 0)
                return;
            Task current;
            do
            {
                current = await Task.WhenAny(allRequiredSteps);
                ReviewFailedAndThrow(current);
                allRequiredSteps.Remove(current);
            } while (allRequiredSteps.Any(s => !s.IsCompleted));
        }


        private List<Task> CreateAndExecuteSteps(CancellationToken cancellationToken)
        {
            Dictionary<Type, StepWrapper> createdSteps = [];

            foreach (StepInfo stepInfo in _allSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IStep? step = CreateStepInstance(stepInfo);
                if (step is null)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step {stepInfo}");
                    continue;
                }
                createdSteps.Add(step.GetType(), new StepWrapper(step));
            }
            List<Task> allRequiredSteps = new();
            foreach (StepInfo stepInfo in _allSteps)
            {
                if (!createdSteps.ContainsKey(stepInfo.StepType))
                {
                    throw new StepDependencyException($"A step {stepInfo} could not be created and initialization cannot proceed.");
                }
                StepWrapper stepWrapper = createdSteps[stepInfo.StepType];

                Task task = ExecuteStep(stepWrapper, stepInfo, createdSteps, cancellationToken);
                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");

                if (stepWrapper.Step.MustInitialize)
                {
                    allRequiredSteps.Add(task);
                }
            }
            return allRequiredSteps;
        }

        private async Task ExecuteStep(StepWrapper stepWrapper, StepInfo stepInfo, Dictionary<Type, StepWrapper> steps, CancellationToken cancellationToken)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                IEnumerable<StepWrapper> dependencies = [];
                foreach (Type type in stepInfo.Dependencies)
                {
                    if (!steps.ContainsKey(type))
                        throw new StepDependencyException($"The dependent step {type.Name} for {stepInfo.StepType.Name} was not created.");
                    dependencies = stepInfo.Dependencies.Select(t => steps[t]);
                }
                await stepWrapper.StartExecute(dependencies, cancellationToken);

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Step {stepWrapper.GetType().Name,-24} executed in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            }
            catch (Exception exception) when (exception is not TaskCanceledException)
            {
                if (stepWrapper.Step.MustInitialize)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Step {stepWrapper.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms",
                            exception);
                    throw;
                }

                if (_logger.IsWarn)
                {
                    _logger.Warn(
                        $"Step {stepWrapper.GetType().Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms {exception}");
                }
            }
            finally
            {
                if (_logger.IsDebug) _logger.Debug($"{stepWrapper.GetType().Name,-24} complete");
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

        private class StepWrapper(IStep step)
        {
            public IStep Step => step;
            public Task StepTask => _taskCompletedSource.Task;

            private TaskCompletionSource _taskCompletedSource = new TaskCompletionSource();

            public async Task StartExecute(IEnumerable<StepWrapper> dependentSteps, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => _taskCompletedSource.TrySetCanceled());

                await Task.WhenAll(dependentSteps.Select(s => s.StepTask));
                try
                {
                    await step.Execute(cancellationToken);
                    _taskCompletedSource.SetResult();
                }
                catch
                {
                    _taskCompletedSource.SetCanceled();
                    throw;
                }
            }
        }
    }

}
