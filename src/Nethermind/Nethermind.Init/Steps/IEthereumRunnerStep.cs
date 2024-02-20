// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;

namespace Nethermind.Init.Steps
{
    public interface IStep
    {
        Task Execute(CancellationToken cancellationToken);

        public bool MustInitialize => true;
    }

    public static class ContainerBuilderExtensions
    {
        public static void RegisterIStepsFromAssembly(this ContainerBuilder builder, Assembly assembly)
        {
            foreach (Type stepType in assembly.GetExportedTypes().Where(IsStepType))
            {
                RegisterIStep(builder, stepType);
            }
        }

        public static void RegisterIStep(this ContainerBuilder builder, Type stepType)
        {
            builder.RegisterType(stepType)
                .AsSelf()
                .As<IStep>()
                .WithAttributeFiltering();

            StepInfo info = new StepInfo(stepType, GetStepBaseType(stepType));
            builder.RegisterInstance(info);
        }

        private static bool IsStepType(Type t) => !t.IsInterface && !t.IsAbstract && typeof(IStep).IsAssignableFrom((Type?)t);
        private static bool IsBaseStepType(Type t) => t != typeof(IStep) && typeof(IStep).IsAssignableFrom((Type?)t);
        private static Type GetStepBaseType(Type type)
        {
            while (type.BaseType is not null && IsBaseStepType(type.BaseType))
            {
                type = type.BaseType;
            }

            return type;
        }
    }
}
