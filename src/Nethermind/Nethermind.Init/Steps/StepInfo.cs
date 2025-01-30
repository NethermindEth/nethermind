// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    public class StepInfo
    {
        public StepInfo(Type type, Type baseType)
        {
            if (type.IsAbstract)
            {
                throw new ArgumentException("Step type cannot be abstract", nameof(type));
            }

            StepType = type;
            StepBaseType = baseType;

            RunnerStepDependenciesAttribute? dependenciesAttribute =
                StepType.GetCustomAttribute<RunnerStepDependenciesAttribute>();
            Dependencies = dependenciesAttribute?.Dependencies ?? [];
        }

        public Type StepBaseType { get; }

        public Type StepType { get; }

        public Type[] Dependencies { get; }

        public override string ToString()
        {
            return $"{StepType.Name} : {StepBaseType.Name}";
        }
    }
}
