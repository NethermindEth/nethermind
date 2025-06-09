// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsManager
    {
        private readonly ILogger _logger;

        private readonly IComponentContext _ctx;
        private readonly IEthereumStepsLoader _loader;

        public EthereumStepsManager(
            IEthereumStepsLoader loader,
            IComponentContext ctx,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(loader);

            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _logger = logManager?.GetClassLogger<EthereumStepsManager>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
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
            Dictionary<Type, StepWrapper> stepInfoMap = [];

            foreach (StepInfo stepInfo in _loader.ResolveStepsImplementations().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                Func<IStep> stepFactory = () =>
                {
                    IStep? step = CreateStepInstance(stepInfo);
                    if (step is null)
                        throw new StepDependencyException(
                            $"A step {stepInfo} could not be created and initialization cannot proceed.");
                    return step;
                };

                Debug.Assert(!stepInfoMap.ContainsKey(stepInfo.StepBaseType), "Resolve steps implementations should have deduplicated step by base type");
                stepInfoMap.Add(stepInfo.StepBaseType, new StepWrapper(stepFactory, stepInfo));
            }

            foreach (var kv in stepInfoMap)
            {
                StepWrapper stepWrapper = kv.Value;
                foreach (Type type in stepWrapper.StepInfo.Dependents)
                {
                    if (stepInfoMap.TryGetValue(type, out StepWrapper? dependent))
                    {
                        dependent.Dependencies.Add(kv.Key);
                    }
                    else
                    {
                        throw new StepDependencyException(
                            $"The dependent step {type.Name} for {stepWrapper.StepInfo.StepBaseType.Name} is missing.");
                    }
                }
            }

            List<Task> allRequiredSteps = new();
            foreach (StepWrapper stepWrapper in stepInfoMap.Values)
            {
                StepInfo stepInfo = stepWrapper.StepInfo;
                Task task = ExecuteStep(stepWrapper, stepInfoMap, cancellationToken);
                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepInfo}");
                allRequiredSteps.Add(task);
            }
            return allRequiredSteps;
        }

        private async Task ExecuteStep(StepWrapper stepWrapper, Dictionary<Type, StepWrapper> stepBaseTypeMap, CancellationToken cancellationToken)
        {
            long startTime = Stopwatch.GetTimestamp();
            try
            {
                List<StepWrapper> dependencies = [];
                foreach (Type type in stepWrapper.Dependencies)
                {
                    if (!stepBaseTypeMap.TryGetValue(type, out StepWrapper? value))
                        throw new StepDependencyException($"The dependent step {type.Name} for {stepWrapper.StepInfo.StepBaseType.Name} was not created.");
                    dependencies.AddRange(value);
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
                step = _ctx.Resolve(stepInfo.StepType) as IStep;
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

        private class StepWrapper(Func<IStep> stepFactory, StepInfo stepInfo)
        {
            public StepInfo StepInfo => stepInfo;

            private IStep? _step;
            public IStep Step => _step ??= stepFactory();
            public Task StepTask => _taskCompletedSource.Task;
            public List<Type> Dependencies = new(stepInfo.Dependencies);

            private TaskCompletionSource _taskCompletedSource = new TaskCompletionSource();

            public async Task StartExecute(IEnumerable<StepWrapper> dependentSteps, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => _taskCompletedSource.TrySetCanceled());

                await Task.WhenAll(dependentSteps.Select(s => s.StepTask));
                try
                {
                    await Step.Execute(cancellationToken);
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
