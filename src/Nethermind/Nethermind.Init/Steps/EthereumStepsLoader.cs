//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Api;

namespace Nethermind.Init.Steps
{
    public class EthereumStepsLoader : IEthereumStepsLoader
    {
        private readonly IEnumerable<Assembly> _stepsAssemblies;
        private readonly Type _baseApiType = typeof(INethermindApi);

        public EthereumStepsLoader(params Assembly[] stepsAssemblies) 
            : this((IEnumerable<Assembly>)stepsAssemblies) { }
        
        public EthereumStepsLoader(IEnumerable<Assembly> stepsAssemblies)
        {
            _stepsAssemblies = stepsAssemblies;
        }
        
        public IEnumerable<StepInfo> LoadSteps(Type apiType)
        {
            if (!apiType.GetInterfaces().Contains(_baseApiType))
            {
                throw new NotSupportedException($"api type must implement {_baseApiType.Name}");
            }
            
            List<Type> allStepTypes = new List<Type>();
            foreach (Assembly stepsAssembly in _stepsAssemblies)
            {
                allStepTypes.AddRange(stepsAssembly.GetExportedTypes()
                    .Where(t => !t.IsInterface && !t.IsAbstract && IsStepType(t)));
            }
            
            return allStepTypes
                .Select(s => new StepInfo(s, GetStepBaseType(s)))
                .GroupBy(s => s.StepBaseType)
                .Select(g => SelectImplementation(g.ToArray(), apiType))
                .Where(s => s != null)
                .Select(s => s!);
        }

        private static bool HasConstructorWithParameter(Type type, Type parameterType)
        {
            Type[] expectedParams = {parameterType};
            return type.GetConstructors().Any(
                c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(expectedParams));
        }

        private StepInfo? SelectImplementation(StepInfo[] stepsWithTheSameBase, Type apiType)
        {
            var stepsWithMatchingApiType = stepsWithTheSameBase
                .Where(t => HasConstructorWithParameter(t.StepType, apiType)).ToArray();

            if (stepsWithMatchingApiType.Length != 1)
            {
                // base API type this time
                stepsWithMatchingApiType = stepsWithTheSameBase
                    .Where(t => HasConstructorWithParameter(t.StepType, _baseApiType)).ToArray();    
            }
            
            if (stepsWithMatchingApiType.Length > 1)
            {
                string stepsDescribed = string.Join(", ", stepsWithTheSameBase.Select(s => s.StepType.Name));
                throw new StepDependencyException(
                    $"Found {stepsWithMatchingApiType.Length} steps with matching API type among {stepsDescribed}");
            }

            return stepsWithMatchingApiType.Length == 1 ? stepsWithMatchingApiType[0] : null;
        }
        
        private static bool IsStepType(Type t) => typeof(IStep).IsAssignableFrom(t);

        private static Type GetStepBaseType(Type type)
        {
            while (type.BaseType != null && IsStepType(type.BaseType))
            {
                type = type.BaseType;
            }

            return type;
        }
    }
}
