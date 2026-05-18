// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsLoader(IConsensusPlugin consensusPlugin, IEnumerable<StepInfo> stepsInfo) : IEthereumStepsLoader
    {
        private readonly IEnumerable<StepInfo> _stepsInfo = stepsInfo;
        private readonly Type _baseApiType = typeof(INethermindApi);
        private readonly Type _apiType = consensusPlugin.ApiType;

        public IEnumerable<StepInfo> ResolveStepsImplementations()
        {
            if (!_apiType.GetInterfaces().Contains(_baseApiType))
            {
                throw new NotSupportedException($"api type must implement {_baseApiType.Name}");
            }

            return _stepsInfo
                .GroupBy(s => s.StepBaseType)
                .Select(g => SelectImplementation(g.ToArray()))
                .Where(s => s is not null)
                .Select(s => s!);
        }

        private static bool HasConstructorWithParameter(Type type, Type parameterType) => type.GetConstructors().Any(
                c => c.GetParameters().Select(p => p.ParameterType).Any(pType => pType == parameterType));

        private StepInfo? SelectImplementation(StepInfo[] stepsWithTheSameBase)
        {
            StepInfo[] stepsWithMatchingApiType = stepsWithTheSameBase
                .Where(t => HasConstructorWithParameter(t.StepType, _apiType)).ToArray();

            if (stepsWithMatchingApiType.Length == 0)
            {
                // base API type this time
                stepsWithMatchingApiType = stepsWithTheSameBase
                    .Where(t => HasConstructorWithParameter(t.StepType, _baseApiType)).ToArray();
            }

            if (stepsWithMatchingApiType.Length > 1)
            {
                stepsWithMatchingApiType.AsSpan().Sort(default(StepInfoByAssignabilityComparer));
            }

            if (stepsWithMatchingApiType.Length == 0)
            {
                // Step without INethermindApi in its constructor
                if (stepsWithTheSameBase.Length == 1) return stepsWithTheSameBase[0];

                throw new StepDependencyException($"Unable to decide step implementation to execute. Steps of same base time: {string.Join(", ", stepsWithTheSameBase.Select(s => s.StepType.Name))}");
            }

            return stepsWithMatchingApiType.FirstOrDefault();
        }

        // Orders steps so the most derived comes first (taken by FirstOrDefault below).
        // The original lambda returned `t1.IsAssignableFrom(t2) ? 1 : -1`, which violated
        // reflexivity (Compare(x, x) = 1) and antisymmetry (for unrelated types both
        // directions returned -1). This restored ordering uses AssemblyQualifiedName as a
        // stable tie-break when neither type derives from the other.
        private readonly struct StepInfoByAssignabilityComparer : IComparer<StepInfo>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(StepInfo? t1, StepInfo? t2)
            {
                if (ReferenceEquals(t1, t2)) return 0;
                if (t1 is null) return -1;
                if (t2 is null) return 1;
                bool t1IsParent = t1.StepType.IsAssignableFrom(t2.StepType);
                bool t2IsParent = t2.StepType.IsAssignableFrom(t1.StepType);
                if (t1IsParent && !t2IsParent) return 1;   // t2 is more derived -> first
                if (t2IsParent && !t1IsParent) return -1;  // t1 is more derived -> first
                return string.CompareOrdinal(t1.StepType.AssemblyQualifiedName, t2.StepType.AssemblyQualifiedName);
            }
        }
    }
}
