// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsLoader : IEthereumStepsLoader
    {
        private readonly IEnumerable<StepInfo> _stepsInfo;
        private readonly Type _baseApiType = typeof(INethermindApi);
        private readonly Type _apiType;

        public EthereumStepsLoader(IConsensusPlugin consensusPlugin, IEnumerable<StepInfo> stepsInfo)
        {
            _stepsInfo = stepsInfo;
            _apiType = consensusPlugin.ApiType;
        }

        public IEnumerable<StepInfo> ResolveStepsImplementations()
        {
            if (!_apiType.GetInterfaces().Contains(_baseApiType))
            {
                throw new NotSupportedException($"api type must implement {_baseApiType.Name}");
            }

            return _stepsInfo
                .GroupBy(s => s.StepBaseType)
                .Select(g => SelectImplementation([.. g]))
                .Where(s => s is not null)
                .Select(s => s!);
        }

        private static bool HasConstructorWithParameter(Type type, Type parameterType)
        {
            return type.GetConstructors().Any(
                c => c.GetParameters().Select(p => p.ParameterType).Any(pType => pType == parameterType));
        }

        private StepInfo? SelectImplementation(StepInfo[] stepsWithTheSameBase)
        {
            StepInfo[] stepsWithMatchingApiType = [.. stepsWithTheSameBase.Where(t => HasConstructorWithParameter(t.StepType, _apiType))];

            if (stepsWithMatchingApiType.Length == 0)
            {
                // base API type this time
                stepsWithMatchingApiType = [.. stepsWithTheSameBase.Where(t => HasConstructorWithParameter(t.StepType, _baseApiType))];
            }

            if (stepsWithMatchingApiType.Length > 1)
            {
                Array.Sort(stepsWithMatchingApiType, (t1, t2) => t1.StepType.IsAssignableFrom(t2.StepType) ? 1 : -1);
            }

            if (stepsWithMatchingApiType.Length == 0)
            {
                // Step without INethermindApi in its constructor
                if (stepsWithTheSameBase.Length == 1) return stepsWithTheSameBase[0];

                throw new StepDependencyException($"Unable to decide step implementation to execute. Steps of same base time: {string.Join(", ", stepsWithTheSameBase.Select(s => s.StepType.Name))}");
            }

            return stepsWithMatchingApiType.FirstOrDefault();
        }
    }
}
