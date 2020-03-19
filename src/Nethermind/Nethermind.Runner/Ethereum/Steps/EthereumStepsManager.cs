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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Runner.Ethereum.Subsystems;

namespace Nethermind.Runner.Ethereum.Steps
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
            var types = GetType().Assembly.GetTypes()
                .Where(t => !t.IsInterface && IsStepType(t))
                .GroupBy(GetStepBaseType);

            Type? GetStepType(Type[] typesInGroup)
            {
                Type? GetStepTypeRecursive(Type? contextType)
                {
                    bool HasConstructorWithParameter(Type? type, Type? parameterType) =>
                        type?.GetConstructors()
                            .Any(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] {parameterType}))
                        ?? false;

                    if (contextType == typeof(object))
                    {
                        return null;
                    }

                    Type stepTypeForContext = typesInGroup.Where(t => !t.IsAbstract)
                        .FirstOrDefault(t => HasConstructorWithParameter(t, contextType));

                    return stepTypeForContext != null
                        ? stepTypeForContext
                        : GetStepTypeRecursive(contextType?.BaseType);
                }

                return typesInGroup.Length == 0 ? typesInGroup[0] : GetStepTypeRecursive(_context.GetType());
            }

            foreach (IGrouping<Type?, Type> typeGroup in types.Where(t => t != null))
            {
                Type? type = GetStepType(typeGroup.ToArray());
                if (type != null)
                {
                    if (_logger.IsDebug) _logger.Debug($"Discovered Ethereum step: {type.Name}");
                    _discoveredSteps[type] = false;

                    Type? baseType = GetStepBaseType(type);
                    if (baseType != null)
                    {
                        _hasFinishedExecution[baseType] = false;
                    }
                }
            }

            ReviewDependencies();
        }

        private readonly ConcurrentDictionary<Type, bool> _hasFinishedExecution = new ConcurrentDictionary<Type, bool>();

        private readonly ConcurrentDictionary<Type, bool> _discoveredSteps = new ConcurrentDictionary<Type, bool>();

        private static bool IsStepType(Type? t) => t != null && typeof(IStep).IsAssignableFrom(t);

        private Type? GetStepBaseType(Type? type) => IsStepType(type?.BaseType) ? GetStepBaseType(type?.BaseType) : type;

        private void ReviewDependencies()
        {
            List<Type> typesReady = new List<Type>();
            bool changedAnything;
            do
            {
                typesReady.Clear();
                changedAnything = false;

                foreach ((Type type, bool allDependenciesInitialized) in _discoveredSteps)
                {
                    if (!allDependenciesInitialized)
                    {
                        RunnerStepDependenciesAttribute? dependenciesAttribute = type.GetCustomAttribute<RunnerStepDependenciesAttribute>();
                        bool allDependenciesFinished = true;
                        if (dependenciesAttribute != null)
                        {
                            foreach (Type dependency in dependenciesAttribute.Dependencies)
                            {
                                if (!_hasFinishedExecution.GetValueOrDefault(dependency))
                                {
                                    allDependenciesFinished = false;
                                    break;
                                }
                            }
                        }

                        if (allDependenciesFinished)
                        {
                            typesReady.Add(type);
                            changedAnything = true;
                        }
                    }
                }

                foreach (Type type in typesReady)
                {
                    _discoveredSteps[type] = true;
                }
            } while (changedAnything);
        }

        public async Task InitializeAll()
        {
            while (_hasFinishedExecution.Values.Any(finished => !finished))
            {
                RunOneRoundOfInitialization();
                ReviewDependencies();
            }

            await Task.WhenAll(_allPending);
        }

        private ConcurrentBag<Task> _allPending = new ConcurrentBag<Task>();
        private ConcurrentBag<Type> _allStarted = new ConcurrentBag<Type>();

        private void RunOneRoundOfInitialization()
        {
            int startedThisRound = 0;
            foreach ((Type discoveredStep, bool dependenciesInitialized) in _discoveredSteps)
            {
                if (_allStarted.Contains(discoveredStep))
                {
                    continue;
                }

                Type? stepBaseType = GetStepBaseType(discoveredStep);
                if (stepBaseType == null)
                {
                    throw new StepDependencyException($"Discovered step is not of step type {discoveredStep}");
                }

                if (_hasFinishedExecution[stepBaseType])
                {
                    continue;
                }

                if (!dependenciesInitialized)
                {
                    continue;
                }

                IStep? step;
                try
                {
                    step = Activator.CreateInstance(discoveredStep, _context) as IStep;
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step of type {discoveredStep}", e);
                    continue;
                }

                if (step == null)
                {
                    if (_logger.IsError) _logger.Error($"Unable to create instance of Ethereum runner step of type {discoveredStep}");
                    continue;
                }

                if (_logger.IsDebug) _logger.Debug($"Executing step: {step.GetType().Name}");

                if (step is ISubsystemStateAware subsystemStateAware)
                {
                    subsystemStateAware.SubsystemStateChanged += SubsystemStateAwareOnSubsystemStateChanged;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                Task task = step.Execute();
                startedThisRound++;
                Task continuationTask = task.ContinueWith(t =>
                {
                    stopwatch.Stop();

                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"Step {step.GetType().Name.PadRight(24)} failed after {stopwatch.ElapsedMilliseconds}ms", t.Exception);
                        _context.LogManager.GetClassLogger().Error($"FAILED TO INIT {stepBaseType.Name}", t.Exception);
                        _hasFinishedExecution[stepBaseType] = true;
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Step {step.GetType().Name.PadRight(24)} executed in {stopwatch.ElapsedMilliseconds}ms");
                        _hasFinishedExecution[stepBaseType] = true;
                    }
                });

                _allStarted.Add(discoveredStep);

                if (step.MustInitialize)
                {
                    _allPending.Add(continuationTask);
                }
                else
                {
                    _hasFinishedExecution[discoveredStep] = true;
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

        private int _foreverLoop;

        private void SubsystemStateAwareOnSubsystemStateChanged(object? sender, SubsystemStateEventArgs e)
        {
            if (!(sender is ISubsystemStateAware subsystemStateAware))
            {
                if (_logger.IsError) _logger.Error($"Received a subsystem state event from an unexpected type of {sender?.GetType()}");
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"{subsystemStateAware.MonitoredSubsystem} state changed to {e.State}");
        }
    }
}