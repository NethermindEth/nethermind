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
}
