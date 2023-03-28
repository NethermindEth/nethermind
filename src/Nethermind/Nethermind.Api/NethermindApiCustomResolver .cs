// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Api;

public class NethermindApiCustomResolver : IServiceProvider
{
    private readonly IServiceProvider _originalServiceProvider;
    private readonly INethermindApi _nethermindApi;

    public NethermindApiCustomResolver(IServiceProvider originalServiceProvider, INethermindApi nethermindApi)
    {
        _originalServiceProvider = originalServiceProvider;
        _nethermindApi = nethermindApi;
    }

    public object GetService(Type serviceType)
    {
        try
        {
            return _originalServiceProvider.GetService(serviceType);
        }
        catch (InvalidOperationException)
        {
        }
        // If the original ServiceProvider cannot resolve, try NethermindApi
        PropertyInfo prop = _nethermindApi.GetType().GetProperties()
            .FirstOrDefault(p => p.PropertyType == serviceType
                    || Nullable.GetUnderlyingType(p.PropertyType) == serviceType
                    || p.PropertyType == Nullable.GetUnderlyingType(serviceType));
        if (prop != null && prop.GetValue(_nethermindApi) is object porpValue)
        {
            return porpValue;
        }

        ServiceDescriptor serviceDescriptor = _nethermindApi.ServiceDescriptors.FirstOrDefault(sd => sd.ServiceType == serviceType);
        Type implementationType = (serviceDescriptor?.ImplementationType)
            ?? throw new Exception($"Service of Type {serviceType.Name} is not defined in the service collection or NethermindApi");
        // Get the constructor with the most parameters
        ConstructorInfo constructor = implementationType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        ParameterInfo[] constructorParameters = constructor.GetParameters();

        object[] constructorArgs = new object[constructorParameters.Length];

        for (int i = 0; i < constructorParameters.Length; i++)
        {
            Type parameterType = constructorParameters[i].ParameterType;

            object parameterInstance;
            try
            {
                parameterInstance = _originalServiceProvider.GetService(parameterType);
            }
            catch (InvalidOperationException)
            {
                parameterInstance = GetService(parameterType);
            }
            if (parameterInstance == null)
            {
                PropertyInfo property = _nethermindApi.GetType().GetProperties()
                    .FirstOrDefault(p => p.PropertyType == parameterType
                    || Nullable.GetUnderlyingType(p.PropertyType) == parameterType
                    || p.PropertyType == Nullable.GetUnderlyingType(parameterType));
                if (property != null && property.GetValue(_nethermindApi) is object propertyValue)
                {
                    parameterInstance = propertyValue;
                }
            }
            // the following exception to the if statement (!HasdefaultValue) could lead to unintentional regressions
            if (parameterInstance is null && !constructorParameters[i].HasDefaultValue)
                throw new InvalidOperationException($"Cannot resolve parameter {parameterType.Name} for constructor {constructor.Name} in type {implementationType.Name}.");
            constructorArgs[i] = parameterInstance;
        }

        // Create the service instance using the constructor and the resolved constructor arguments
        return constructor.Invoke(constructorArgs);
    }
}
