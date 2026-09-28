// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsManager
    {
        private readonly ILogger _logger;

        private readonly IComponentContext _ctx;
        private readonly IEthereumStepsLoader _loader;
        private readonly StepTarget[] _targets;
        private readonly StepCommandSelection[] _commandSelections;

        public EthereumStepsManager(
            IEthereumStepsLoader loader,
            IComponentContext ctx,
            IEnumerable<StepTarget> targets,
            IEnumerable<StepCommandSelection> commandSelections,
            ILogManager logManager)
        {
            ArgumentNullException.ThrowIfNull(loader);

            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _logger = logManager?.GetClassLogger<EthereumStepsManager>()
                      ?? throw new ArgumentNullException(nameof(logManager));

            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            _targets = targets.ToArray();
            _commandSelections = commandSelections.ToArray();
        }

        /// <summary>Whether this run is a one-shot command rather than a node start.</summary>
        public bool HasTarget => _targets.Length > 0 || _commandSelections.Length > 0;

        /// <summary>Runs the step graph, or the target's closure when this run is a command.</summary>
        /// <exception cref="OperationCanceledException">Shutdown was requested before the target finished.</exception>
        /// <exception cref="StepDependencyException">The target did not complete.</exception>
        public async Task InitializeAll(CancellationToken cancellationToken)
        {
            (List<Task> allRequiredSteps, Task? targetTask) = CreateAndExecuteSteps(cancellationToken);
            if (allRequiredSteps.Count != 0)
            {
                do
                {
                    Task current = await Task.WhenAny(allRequiredSteps);
                    ReviewFailedAndThrow(current);
                    if (current.IsCanceled && _logger.IsDebug) _logger.Debug("A required step was cancelled!");
                    allRequiredSteps.Remove(current);
                } while (allRequiredSteps.Any(s => !s.IsCompleted));
            }

            if (!HasTarget) return;

            // A command's exit code is the target's outcome, so anything short of it completing has to surface
            // as an exception for Program to map. Review every task first, not just the ones the loop happened
            // to pick up: when the last tasks complete together the loop stops with one of them unreviewed, and
            // a failing ancestor cancels the target before its own task faults, so the real error is there.
            foreach (Task step in allRequiredSteps) ReviewFailedAndThrow(step);

            // Shutdown counts as not completing; the exit code is already SigInt and must not be overwritten.
            cancellationToken.ThrowIfCancellationRequested();

            if (targetTask?.IsCompletedSuccessfully != true)
                throw new StepDependencyException("The command step did not complete.");
        }


        internal async Task InitializeThrough(Type target, CancellationToken cancellationToken, params Type[] skippedSteps)
        {
            (List<Task> steps, _) = CreateAndExecuteSteps(cancellationToken, target, skippedSteps);
            await Task.WhenAll(steps);
        }

        private (List<Task> AllSteps, Task? TargetTask) CreateAndExecuteSteps(CancellationToken cancellationToken, Type? target = null, params Type[] skippedSteps)
        {
            Dictionary<Type, StepWrapper> stepInfoMap = [];
            List<StepInfo> resolvedSteps = _loader.ResolveStepsImplementations().ToList();
            target ??= ResolveTarget(resolvedSteps);

            foreach (StepInfo stepInfo in resolvedSteps)
            {
                // Command steps are jobs in their own right, not part of a node start, so they only run when
                // selected. Excluding them here rather than after the graph is built keeps the dependency
                // validation below meaningful: no edge is ever folded in for a step that will not run.
                if (stepInfo.Command is not null && stepInfo.StepBaseType != target) continue;
                cancellationToken.ThrowIfCancellationRequested();

                IStep StepFactory() => CreateStepInstance(stepInfo);

                Debug.Assert(!stepInfoMap.ContainsKey(stepInfo.StepBaseType), "Resolve steps implementations should have deduplicated step by base type");
                stepInfoMap.Add(stepInfo.StepBaseType, new StepWrapper(StepFactory, stepInfo));
            }

            foreach ((Type key, StepWrapper stepWrapper) in stepInfoMap)
            {
                foreach (Type type in stepWrapper.StepInfo.Dependents)
                {
                    if (stepInfoMap.TryGetValue(type, out StepWrapper? dependent))
                    {
                        dependent.Dependencies.Add(key);
                    }
                    else
                    {
                        throw new StepDependencyException($"The dependent step {type.Name} for {stepWrapper.StepInfo.StepBaseType.Name} is missing.");
                    }
                }

                // Remove absent optional dependencies
                for (int i = 0; i < stepWrapper.Dependencies.Count; i++)
                {
                    Type dep = stepWrapper.Dependencies[i];
                    if (!stepInfoMap.ContainsKey(dep))
                    {
                        if (dep.GetCustomAttribute<RunnerStepDependenciesAttribute>()?.Optional is true)
                        {
                            stepWrapper.Dependencies.RemoveAt(i);
                            i--;
                            if (_logger.IsDebug) _logger.Debug($"Optional dependency {dep.Name} will not be loaded for step {key.Name} because it was not found.");
                        }
                        else
                        {
                            throw new StepDependencyException($"Dependency {dep.Name} was not found for step {key.Name}.");
                        }
                    }
                }
            }

            foreach (Type skipped in skippedSteps)
            {
                if (stepInfoMap.TryGetValue(skipped, out StepWrapper? step))
                {
                    step.Dependencies.Clear();
                    step.MarkCompleted();
                }
            }
            if (target is not null) PruneToTarget(stepInfoMap, target);

            if (_logger.IsDebug) _logger.Debug($"Ethereum steps dependency tree:\n{BuildStepDependencyTree(stepInfoMap)}");
            List<Task> allRequiredSteps = [];
            Task? targetTask = null;
            foreach ((Type stepBaseType, StepWrapper stepWrapper) in stepInfoMap)
            {
                Task stepTask = Array.IndexOf(skippedSteps, stepBaseType) >= 0
                    ? Task.CompletedTask : ExecuteStep(stepWrapper, stepInfoMap, cancellationToken);
                allRequiredSteps.Add(stepTask);
                if (stepBaseType == target) targetTask = stepTask;
            }
            return (allRequiredSteps, targetTask);
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

                await stepWrapper.WaitForDependencies(dependencies, cancellationToken);

                if (_logger.IsDebug) _logger.Debug($"Executing step: {stepWrapper.StepInfo}");

                await stepWrapper.RunStep(cancellationToken);

                if (_logger.IsDebug) _logger.Debug($"Step {stepWrapper.StepInfo.StepType.Name,-24} executed in {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms");
            }
            catch (Exception exception) when (exception is not TaskCanceledException)
            {
                if (stepWrapper.Step.MustInitialize)
                {
                    if (_logger.IsError) _logger.Error($"Step {stepWrapper.StepInfo.StepType.Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms", exception);
                    throw;
                }

                if (_logger.IsWarn) _logger.Warn($"Step {stepWrapper.StepInfo.StepType.Name,-24} failed after {Stopwatch.GetElapsedTime(startTime).TotalMilliseconds:N0}ms {exception}");
            }
            finally
            {
                if (_logger.IsDebug) _logger.Debug($"{stepWrapper.StepInfo.StepType.Name,-24} complete");
            }
        }

        /// <summary>Determines the single step this run exists to execute, if any.</summary>
        /// <remarks>
        /// Merges the targets selected by modules with the command name given on the command line. Targets are
        /// one-shot jobs over the same databases, so selecting more than one distinct step is rejected rather
        /// than run as a union.
        /// </remarks>
        /// <exception cref="InvalidConfigurationException">
        /// The requested command is not registered, or more than one distinct target was selected.
        /// </exception>
        private Type? ResolveTarget(IReadOnlyList<StepInfo> resolvedSteps)
        {
            HashSet<Type> targets = [];
            foreach (StepTarget target in _targets) targets.Add(target.StepBaseType);

            foreach (StepCommandSelection selection in _commandSelections)
            {
                StepInfo[] matches = [.. resolvedSteps.Where(step =>
                    string.Equals(step.Command, selection.Name, StringComparison.OrdinalIgnoreCase))];

                if (matches.Length == 0)
                    throw new InvalidConfigurationException(
                        $"Unknown command '{selection.Name}'. {DescribeAvailableCommands(resolvedSteps)}",
                        ExitCodes.UnrecognizedOption);

                // Commands come from whatever steps are registered, including a plugin's, so two of them can
                // claim the same name. Picking one silently would make which job runs depend on plugin order.
                if (matches.Length > 1)
                    throw new InvalidConfigurationException(
                        $"Command '{selection.Name}' is claimed by more than one step: {string.Join(", ", matches.Select(static s => s.StepType.FullName).Order())}.",
                        ExitCodes.ConflictingConfigurations);

                targets.Add(matches[0].StepBaseType);
            }

            if (targets.Count > 1)
                throw new InvalidConfigurationException(
                    $"Only one command can run at a time, but these were all selected: {string.Join(", ", targets.Select(t => DescribeTarget(resolvedSteps, t)).Order())}.",
                    ExitCodes.ConflictingConfigurations);

            return targets.FirstOrDefault();
        }

        /// <summary>Names a target the way the operator asked for it — by command where it has one.</summary>
        private static string DescribeTarget(IReadOnlyList<StepInfo> resolvedSteps, Type stepBaseType)
        {
            string? command = resolvedSteps.FirstOrDefault(step => step.StepBaseType == stepBaseType)?.Command;
            return command is null ? stepBaseType.Name : $"'{command}'";
        }

        private static string DescribeAvailableCommands(IReadOnlyList<StepInfo> resolvedSteps)
        {
            StepInfo[] commands = [.. resolvedSteps.Where(static step => step.Command is not null).OrderBy(static step => step.Command, StringComparer.Ordinal)];
            if (commands.Length == 0) return "No commands are available.";

            int width = commands.Max(static step => step.Command!.Length);
            StringBuilder sb = new("Available commands:");
            foreach (StepInfo command in commands)
                sb.Append("\n  ").Append(command.Command!.PadRight(width)).Append("  ").Append(command.CommandDescription);

            return sb.ToString();
        }

        /// <summary>Restricts execution to the target and its transitive dependencies.</summary>
        /// <remarks>
        /// Runs after <c>Dependents</c> edges have been folded into the dependency map, so a step declaring
        /// itself a dependent of something inside the closure is pulled in with it. <see cref="StepWrapper.Step"/>
        /// is lazy, so pruned steps are never resolved from the container and their dependencies are never built.
        /// </remarks>
        /// <exception cref="StepDependencyException">The target step is not registered.</exception>
        private static void PruneToTarget(Dictionary<Type, StepWrapper> stepInfoMap, Type target)
        {
            if (!stepInfoMap.ContainsKey(target))
                throw new StepDependencyException(
                    $"Target step {target.Name} is not registered. Registered steps: {string.Join(", ", stepInfoMap.Keys.Select(static t => t.Name).Order())}.");

            HashSet<Type> required = [];
            Stack<Type> toVisit = new([target]);
            while (toVisit.TryPop(out Type? stepBaseType))
            {
                if (!required.Add(stepBaseType)) continue;
                foreach (Type dependency in stepInfoMap[stepBaseType].Dependencies) toVisit.Push(dependency);
            }

            foreach (Type stepBaseType in stepInfoMap.Keys.Where(t => !required.Contains(t)).ToArray())
                stepInfoMap.Remove(stepBaseType);
        }

        private IStep CreateStepInstance(StepInfo stepInfo)
        {
            try
            {
                return (_ctx.Resolve(stepInfo.StepType) as IStep)!;
            }
            catch (Exception e)
            {
                if (TryUnwrapException(e, out Exception? unwrappedException))
                {
                    ExceptionDispatchInfo.Capture(unwrappedException!).Throw();
                    throw;
                }

                throw new StepDependencyException($"A step {stepInfo} could not be created and initialization cannot proceed.", e);
            }
        }

        private bool TryUnwrapException(Exception exception, out Exception? unwrapped)
        {
            unwrapped = exception;
            while (unwrapped is DependencyResolutionException resolutionException)
            {
                unwrapped = resolutionException.InnerException;
            }

            return unwrapped is InvalidConfigurationException;
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
            Dictionary<string, List<string>> depsMap = stepInfoMap.ToDictionary(
                kv => kv.Key.Name,
                kv => kv.Value.Dependencies.Select(d => d.Name).ToList()
            );

            // Build children map for topological sorting (parent -> children)
            Dictionary<string, List<string>> dependentsMap = stepInfoMap.Keys.ToDictionary(t => t.Name, t => new List<string>());
            foreach (KeyValuePair<Type, StepWrapper> kv in stepInfoMap)
            {
                string node = kv.Key.Name;
                foreach (Type dependency in kv.Value.Dependencies)
                {
                    if (dependentsMap.ContainsKey(dependency.Name))
                        dependentsMap[dependency.Name].Add(node);
                }
            }

            // Kahn's algorithm to compute topological order
            Dictionary<string, int> inDegree = depsMap.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            Queue<string> queue = new(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy((c) => dependentsMap[c].Count));
            List<string> sorted = [];
            Dictionary<string, int> degree = new(inDegree);
            while (queue.Count > 0)
            {
                string n = queue.Dequeue();
                sorted.Add(n);
                foreach (string? dependent in dependentsMap[n].OrderBy((c) => dependentsMap[c].Count))
                {
                    degree[dependent]--;
                    if (degree[dependent] == 0)
                        queue.Enqueue(dependent);
                }
            }
            if (sorted.Count != depsMap.Count)
                sorted = depsMap.Keys.OrderBy(n => n).ToList();

            // Compute max dependency depth for indentation
            Dictionary<string, int> depth = [];
            Dictionary<string, HashSet<string>> allCombinedDeps = [];
            foreach (string node in sorted)
            {
                List<string> deps = depsMap[node];
                depth[node] = deps.Count == 0
                    ? 0
                    : deps.Select(d => depth.GetValueOrDefault(d, 0)).Max() + 1;

                allCombinedDeps[node] = depsMap[node].SelectMany((d) => allCombinedDeps[d]).ToHashSet();
                allCombinedDeps[node].AddRange(depsMap[node]);
            }

            Dictionary<string, List<string>> deduplicatedDependency = [];
            foreach (string node in sorted)
            {
                HashSet<string> childOnlyAllCombinedDeps = depsMap[node].SelectMany(d => allCombinedDeps[d]).ToHashSet();
                deduplicatedDependency[node] = depsMap[node].Where(d => !childOnlyAllCombinedDeps.Contains(d)).ToList();
            }

            // Build the indented output using reversed indentation
            StringBuilder sb = new();
            foreach (string node in sorted)
            {
                int lvl = depth[node];
                sb.Append(new string(' ', lvl * 2));
                List<string> deps = deduplicatedDependency[node];
                sb.Append(dependentsMap[node].Count == 0 ? "● " : "○ ");
                sb.Append(node);
                sb.AppendLine(deps.Count != 0 ? $" (depends on {string.Join(", ", deps)})" : "");
            }

            return sb.ToString();
        }

        private class StepWrapper(Func<IStep> stepFactory, StepInfo stepInfo)
        {
            public StepInfo StepInfo => stepInfo;

            private IStep? _step;
            public IStep Step => _step ??= stepFactory();
            private Task StepTask => _taskCompletedSource.Task;
            public readonly List<Type> Dependencies = [.. stepInfo.Dependencies];

            private readonly TaskCompletionSource _taskCompletedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task WaitForDependencies(IEnumerable<StepWrapper> dependentSteps, CancellationToken cancellationToken)
            {
                cancellationToken.Register(() => _taskCompletedSource.TrySetCanceled());

                await Task.WhenAll(dependentSteps.Select(s => s.StepTask));
            }

            public void MarkCompleted() => _taskCompletedSource.TrySetResult();

            public async Task RunStep(CancellationToken cancellationToken)
            {
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
