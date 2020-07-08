//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class EthereumStepsLoader : IEthereumStepsLoader
    {
        private readonly Assembly[] _stepsAssemblies;
        private readonly Type _baseContextType = typeof(EthereumRunnerContext);

        public EthereumStepsLoader(params Assembly[] stepsAssemblies)
        {
            _stepsAssemblies = stepsAssemblies;
        }
        
        public IEnumerable<StepInfo> LoadSteps(Type contextType)
        {
            if (contextType != _baseContextType
                && contextType.BaseType != _baseContextType)
            {
                throw new NotSupportedException("Multilevel inheritance context are not supported");
            }
            
            List<Type> allStepTypes = new List<Type>();
            foreach (Assembly stepsAssembly in _stepsAssemblies)
            {
                allStepTypes.AddRange(stepsAssembly.GetTypes()
                    .Where(t => !t.IsInterface && !t.IsAbstract && IsStepType(t)));
            }
            
            return allStepTypes
                .Select(s => new StepInfo(s, GetStepBaseType(s)))
                .GroupBy(s => s.StepBaseType)
                .Select(g => SelectImplementation(g.ToArray(), contextType))
                .Where(s => s != null)
                .Select(s => s!);
        }

        private static bool HasConstructorWithParameter(Type type, Type parameterType)
        {
            Type[] expectedParams = {parameterType};
            return type.GetConstructors().Any(
                c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(expectedParams));
        }

        private StepInfo? SelectImplementation(StepInfo[] stepsWithTheSameBase, Type contextType)
        {
            var stepsWithMatchingContextType = stepsWithTheSameBase
                .Where(t => HasConstructorWithParameter(t.StepType, contextType)).ToArray();

            if (stepsWithMatchingContextType.Length != 1)
            {
                // base context type this time
                stepsWithMatchingContextType = stepsWithTheSameBase
                    .Where(t => HasConstructorWithParameter(t.StepType, _baseContextType)).ToArray();    
            }
            
            if (stepsWithMatchingContextType.Length > 1)
            {
                string stepsDescribed = string.Join(", ", stepsWithTheSameBase.Select(s => s.StepType.Name));
                throw new StepDependencyException(
                    $"Found {stepsWithMatchingContextType.Length} steps with matching context type among {stepsDescribed}");
            }

            return stepsWithMatchingContextType.Length == 1 ? stepsWithMatchingContextType[0] : null;
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