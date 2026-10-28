// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;

namespace Nethermind.Api.Steps;

public static class ContainerBuilderExtensions
{
    public static ContainerBuilder AddStep(this ContainerBuilder builder, StepInfo stepInfo)
    {
        builder.AddSingleton<StepInfo>(stepInfo);
        builder.RegisterType(stepInfo.StepType).WithAttributeFiltering().SingleInstance();
        return builder;
    }

    /// <summary>Makes the given step the goal of this run.</summary>
    /// <remarks>
    /// Only the target and its transitive dependencies execute, and the process exits once the target completes.
    /// The step must be registered separately with <see cref="AddStep"/>; this only selects it.
    /// </remarks>
    public static ContainerBuilder SelectStepTarget(this ContainerBuilder builder, StepInfo stepInfo) =>
        builder.AddSingleton(new StepTarget(stepInfo.StepBaseType));
}
