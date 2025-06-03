// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps
{
    [AttributeUsage(AttributeTargets.Class)]
    public class RunnerStepDependenciesAttribute(params Type[] dependencies) : Attribute
    {
        public Type[] Dependencies { get; } = dependencies;
    }
}
