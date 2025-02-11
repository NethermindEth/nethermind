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
            do
            {
                Task current = await Task.WhenAny(allRequiredSteps);
                ReviewFailedAndThrow(current);
                if (current.IsCanceled && _logger.IsDebug)
                    _logger.Debug($"A required step was cancelled!");
                allRequiredSteps.Remove(current);
            } while (allRequiredSteps.Any(s => !s.IsCompleted));
        }


        private List<Task> CreateAndExecuteSteps(CancellationToken cancellationToken)
        {
            Dictionary<Type, List<StepWrapper>> stepBaseTypeMap = [];
            Dictionary<Type, StepInfo> stepInfoMap = []; 

            foreach (StepInfo stepInfo in _allSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IStep? step = CreateStepInstance(stepInfo);
                if (step is null)
                    throw new StepDependencyException($"A step {stepInfo} could not be created and initialization cannot proceed.");
                stepInfoMap.Add(step.GetType(), stepInfo);
                if (stepBaseTypeMap.ContainsKey(stepInfo.StepBaseType))
                {
                    stepBaseTypeMap[stepInfo.StepBaseType].Add(new StepWrapper(step));
                }
                else
                {
                    stepBaseTypeMap.Add(stepInfo.StepBaseType, new List<StepWrapper>() { new StepWrapper(step) });
                }
            }
            List<Task> allRequiredSteps = new();
            foreach (List<StepWrapper> steps in stepBaseTypeMap.Values)
            {
                foreach (StepWrapper stepWrapper in steps)
                {
                    StepInfo stepInfo = stepInfoMap[stepWrapper.Step.GetType()];
                    Task task = ExecuteStep(stepWrapper, stepInfo, stepBaseTypeMap, cancellationToken);
                    if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");

                    if (stepWrapper.Step.MustInitialize)
                    {
                        allRequiredSteps.Add(task);
                    }
                }
            }
            return allRequiredSteps;
        }

        private async Task ExecuteStep(StepWrapper stepWrapper, StepInfo stepInfo, Dictionary<Type, List<StepWrapper>> stepBaseTypeMap, CancellationToken cancellationToken)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                List<StepWrapper> dependencies = [];
                foreach (Type type in stepInfo.Dependencies)
                {
                    if (!stepBaseTypeMap.ContainsKey(type))
                        throw new StepDependencyException($"The dependent step {type.Name} for {stepInfo.StepType.Name} was not created.");
                    foreach (Type dependency in stepInfo.Dependencies)
                    {
                        dependencies.AddRange(stepBaseTypeMap[dependency]);
                    }
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
                    _taskCompletedSource.TrySetResult();
                }
                catch
                {
                    //TaskCompletionSource is transitioned to cancelled state to prevent a cascade effect of log statements
                    _taskCompletedSource.TrySetCanceled();
                    throw;
                }
            }
        }
    }

}
