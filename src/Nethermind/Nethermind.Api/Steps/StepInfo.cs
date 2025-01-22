// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

namespace Nethermind.Api.Steps
{
    public class StepInfo
    {
        public StepInfo(Type type)
        {
            if (type.IsAbstract)
            {
                throw new ArgumentException("Step type cannot be abstract", nameof(type));
            }

            StepType = type;
            StepBaseType = GetStepBaseType(type);

            RunnerStepDependenciesAttribute? dependenciesAttribute =
                StepType.GetCustomAttribute<RunnerStepDependenciesAttribute>();
            Dependencies = dependenciesAttribute?.Dependencies ?? [];
        }

        public Type StepBaseType { get; }

        public Type StepType { get; }

        public Type[] Dependencies { get; }

        public override string ToString()
        {
            // return $"{StepType.Name} : {StepBaseType.Name} ({Stage})";
            return $"{StepType.Name} : {StepBaseType.Name}";
        }

        private static Type GetStepBaseType(Type type)
        {
            while (type.BaseType is not null && IsStepType(type.BaseType))
            {
                type = type.BaseType;
            }

            return type;
        }

        public static bool IsStepType(Type t) => typeof(IStep).IsAssignableFrom(t);
    }
}
