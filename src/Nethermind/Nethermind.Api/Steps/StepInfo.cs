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

            if (!IsStepType(type))
            {
                throw new ArgumentException($"{type.FullName} is not a step type");
            }

            StepType = type;
            StepBaseType = GetStepBaseType(type);

            RunnerStepDependenciesAttribute? dependenciesAttribute =
                StepType.GetCustomAttribute<RunnerStepDependenciesAttribute>();
            Dependencies = dependenciesAttribute?.Dependencies ?? [];
            Dependents = dependenciesAttribute?.Dependents ?? [];

            StepCommandAttribute? commandAttribute = StepType.GetCustomAttribute<StepCommandAttribute>();
            Command = commandAttribute?.Name;
            CommandDescription = commandAttribute?.Description;
        }

        /// <summary>The command name this step is exposed as, or <c>null</c> for an ordinary startup step.</summary>
        /// <remarks>A step with a command only runs when it is selected as the target of the run.</remarks>
        public string? Command { get; }

        /// <summary>One line describing <see cref="Command"/>.</summary>
        public string? CommandDescription { get; }

        public Type StepBaseType { get; }

        public Type StepType { get; }

        public Type[] Dependencies { get; }
        public Type[] Dependents { get; }

        public override string ToString() => $"{StepType.Name} : {StepBaseType.Name}";

        private static Type GetStepBaseType(Type type)
        {
            while (type.BaseType is not null && IsStepType(type.BaseType))
            {
                type = type.BaseType;
            }

            return type;
        }

        public static implicit operator StepInfo(Type type) => new(type);

        public static bool IsStepType(Type t) => typeof(IStep).IsAssignableFrom(t);
    }
}
