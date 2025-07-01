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
using Nethermind.Core.Collections;
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

            if (_logger.IsDebug) _logger.Debug($"Ethereum steps dependency tree:\n{BuildStepDependencyTree(stepInfoMap)}");
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

        /// <summary>
        /// Recursively prints roots (steps with no dependencies) and their dependents.
        /// </summary>
        private string BuildStepDependencyTree(Dictionary<Type, StepWrapper> stepInfoMap)
        {
            // Map each step to its direct dependencies
            var depsMap = stepInfoMap.ToDictionary(
                kv => kv.Key.Name,
                kv => kv.Value.Dependencies.Select(d => d.Name).ToList()
            );

            // Build children map for topological sorting (parent -> children)
            var dependentsMap = stepInfoMap.Keys.ToDictionary(t => t.Name, t => new List<string>());
            foreach (var kv in stepInfoMap)
            {
                var node = kv.Key.Name;
                foreach (var dependency in kv.Value.Dependencies)
                {
                    if (dependentsMap.ContainsKey(dependency.Name))
                        dependentsMap[dependency.Name].Add(node);
                }
            }

            // Kahn's algorithm to compute topological order
            var inDegree = depsMap.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy((c) => dependentsMap[c].Count));
            var sorted = new List<string>();
            var degree = new Dictionary<string, int>(inDegree);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                sorted.Add(n);
                foreach (var dependent in dependentsMap[n].OrderBy((c) => dependentsMap[c].Count))
                {
                    degree[dependent]--;
                    if (degree[dependent] == 0)
                        queue.Enqueue(dependent);
                }
            }
            if (sorted.Count != depsMap.Count)
                sorted = depsMap.Keys.OrderBy(n => n).ToList();

            // Compute max dependency depth for indentation
            var depth = new Dictionary<string, int>();
            var allCombinedDeps = new Dictionary<string, HashSet<string>>();
            foreach (var node in sorted)
            {
                var deps = depsMap[node];
                depth[node] = deps.Count == 0
                    ? 0
                    : deps.Select(d => depth.GetValueOrDefault(d, 0)).Max() + 1;

                allCombinedDeps[node] = depsMap[node].SelectMany((d) => allCombinedDeps[d]).ToHashSet();
                allCombinedDeps[node].AddRange(depsMap[node]);
            }

            var deduplicatedDependency = new Dictionary<string, List<string>>();
            foreach (var node in sorted)
            {
                HashSet<string> childOnlyAllCombinedDeps = depsMap[node].SelectMany((d) => allCombinedDeps[d]).ToHashSet();
                deduplicatedDependency[node] = depsMap[node].Where((d) => !childOnlyAllCombinedDeps.Contains(d)).ToList();
            }

            // Build the indented output using reversed indentation
            var sb = new System.Text.StringBuilder();
            foreach (var node in sorted)
            {
                int lvl = depth[node];
                sb.Append(new string(' ', lvl * 2));
                var deps = deduplicatedDependency[node];
                if (dependentsMap[node].Count == 0)
                {
                    sb.Append("● ");
                }
                else
                {
                    sb.Append("○ ");
                }

                if (deps.Count == 0)
                    sb.AppendLine($"{node}");
                else
                    sb.AppendLine($"{node} (depends on {string.Join(", ", deps)})");
            }
            return sb.ToString();
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
