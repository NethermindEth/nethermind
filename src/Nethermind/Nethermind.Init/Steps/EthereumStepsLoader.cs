// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Api;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsLoader : IEthereumStepsLoader
    {
        private readonly IEnumerable<Assembly> _stepsAssemblies;
        private readonly Type _baseApiType = typeof(INethermindApi);

        public EthereumStepsLoader(params Assembly[] stepsAssemblies)
            : this((IEnumerable<Assembly>)stepsAssemblies) { }

        public EthereumStepsLoader(IEnumerable<Assembly> stepsAssemblies)
        {
            _stepsAssemblies = stepsAssemblies;
        }

        public IEnumerable<StepInfo> LoadSteps(Type apiType)
        {
            if (!apiType.GetInterfaces().Contains(_baseApiType))
            {
                throw new NotSupportedException($"api type must implement {_baseApiType.Name}");
            }

            List<Type> allStepTypes = new List<Type>();
            foreach (Assembly stepsAssembly in _stepsAssemblies)
            {
                allStepTypes.AddRange(stepsAssembly.GetExportedTypes()
                    .Where(t => !t.IsInterface && !t.IsAbstract && IsStepType(t)));
            }

            return allStepTypes
                .Select(s => new StepInfo(s, GetStepBaseType(s)))
                .GroupBy(s => s.StepBaseType)
                .Select(g => SelectImplementation(g.ToArray(), apiType))
                .Where(s => s is not null)
                .Select(s => s!);
        }

        private static bool HasConstructorWithParameter(Type type, Type parameterType)
        {
            Type[] expectedParams = { parameterType };
            return type.GetConstructors().Any(
                c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(expectedParams));
        }

        private StepInfo? SelectImplementation(StepInfo[] stepsWithTheSameBase, Type apiType)
        {
            StepInfo[] stepsWithMatchingApiType = stepsWithTheSameBase
                .Where(t => HasConstructorWithParameter(t.StepType, apiType)).ToArray();

            if (stepsWithMatchingApiType.Length == 0)
            {
                // base API type this time
                stepsWithMatchingApiType = stepsWithTheSameBase
                    .Where(t => HasConstructorWithParameter(t.StepType, _baseApiType)).ToArray();
            }

            if (stepsWithMatchingApiType.Length > 1)
            {
                Array.Sort(stepsWithMatchingApiType, (t1, t2) => t1.StepType.IsAssignableFrom(t2.StepType) ? 1 : -1);
            }

            return stepsWithMatchingApiType.FirstOrDefault();
        }

        private static bool IsStepType(Type t) => typeof(IStep).IsAssignableFrom(t);

        private static Type GetStepBaseType(Type type)
        {
            while (type.BaseType is not null && IsStepType(type.BaseType))
            {
                type = type.BaseType;
            }

            return type;
        }
    }
}
