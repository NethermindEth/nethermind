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
using System.Reflection;

namespace Nethermind.Init.Steps
{
    public class StepInfo
    {
        public StepInfo(Type type, Type baseType)
        {
            if (type.IsAbstract)
            {
                throw new ArgumentException("Step type cannot be abstract", nameof(type));
            }
            
            StepType = type;
            StepBaseType = baseType;
            
            RunnerStepDependenciesAttribute? dependenciesAttribute =
                StepType.GetCustomAttribute<RunnerStepDependenciesAttribute>();
            Dependencies = dependenciesAttribute?.Dependencies ?? Array.Empty<Type>();
        }

        public Type StepBaseType { get; }

        public Type StepType { get; }
        
        public Type[] Dependencies { get; }

        public StepInitializationStage Stage { get; set; }

        public override string ToString()
        {
            return $"{StepType.Name} : {StepBaseType.Name} ({Stage})";
        }
    }
}
