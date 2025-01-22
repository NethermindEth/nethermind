// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Init.Steps
{
    [AttributeUsage(AttributeTargets.Class)]
    public class RunnerStepDependenciesAttribute : Attribute
    {
        public Type[] Dependencies { get; }

        public RunnerStepDependenciesAttribute(params Type[] dependencies)
        {
            Dependencies = dependencies;
        }
    }
}
