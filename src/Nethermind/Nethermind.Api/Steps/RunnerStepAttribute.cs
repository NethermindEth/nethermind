// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps;

[AttributeUsage(AttributeTargets.Class)]
public class RunnerStepDependenciesAttribute(Type[] dependencies, Type[] dependents) : Attribute
{
    public RunnerStepDependenciesAttribute(params Type[] dependencies) : this(dependencies, []) { }

    public Type[] Dependencies { get; } = dependencies;
    public Type[] Dependents { get; } = dependents;
    public bool Optional { get; set; }
}
