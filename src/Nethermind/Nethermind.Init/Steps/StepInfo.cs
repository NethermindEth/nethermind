// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

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
            Dependencies = dependenciesAttribute?.Dependencies ?? Array.Empty<Type>();
        }

        public Type StepBaseType { get; }

        public Type StepType { get; }

        public Type[] Dependencies { get; }

        public StepInitializationStage Stage { get; set; }

        public override string ToString()
        {
            return $"{StepType.Name} : {StepBaseType.Name} ({Stage})";
        }
    }
}
