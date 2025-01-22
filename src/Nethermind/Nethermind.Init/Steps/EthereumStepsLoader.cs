// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        private readonly IEnumerable<StepInfo> _stepsInfo;
        private readonly Type _baseApiType = typeof(INethermindApi);

        public EthereumStepsLoader(IEnumerable<StepInfo> stepsInfo)
        {
            _stepsInfo = stepsInfo;
        }

        public IEnumerable<StepInfo> ResolveStepsImplementations(Type apiType)
        {
            if (!apiType.GetInterfaces().Contains(_baseApiType))
            {
                throw new NotSupportedException($"api type must implement {_baseApiType.Name}");
            }

            return _stepsInfo
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
    }
}
