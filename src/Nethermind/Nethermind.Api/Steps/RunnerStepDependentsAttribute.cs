// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps;

[AttributeUsage(AttributeTargets.Class)]
public class RunnerStepDependentsAttribute(params Type[] dependents) : Attribute
{
    public Type[] Dependencies { get; } = dependents;
}
