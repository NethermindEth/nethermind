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

namespace Nethermind.Abi
{
    public static class AbiDefinitionExtensions
    {
        public static void ReplaceAbiTypes(this AbiDefinition abiDefinition, params AbiType[] typesToReplace)
        {
            void ChangeParameters(AbiParameter[] parameters, params AbiType[] typesToChange)
            {
                for (int index = 0; index < parameters.Length; index++)
                {
                    AbiParameter parameter = parameters[index];
                    for (int i = 0; i < typesToChange.Length; i++)
                    {
                        if (parameter.Type.Name == typesToChange[i].Name)
                        {
                            parameter.Type = typesToChange[i];
                        }
                    }
                }
            }

            AbiType[] abiTypesToReplace = new AbiType[2 * typesToReplace.Length];
            
            for (int i = 0; i < typesToReplace.Length; i++)
            {
                abiTypesToReplace[2 * i] = typesToReplace[i];
                abiTypesToReplace[2 * i + 1] = new AbiArray(typesToReplace[i]);
            }
            
            foreach (AbiFunctionDescription function in abiDefinition.Functions.Values)
            {
                ChangeParameters(function.Inputs, abiTypesToReplace);
                ChangeParameters(function.Outputs, abiTypesToReplace);
            }

            foreach (AbiFunctionDescription function in abiDefinition.Constructors)
            {
                ChangeParameters(function.Inputs, abiTypesToReplace);
                ChangeParameters(function.Outputs, abiTypesToReplace);
            }

            foreach (AbiEventDescription eventDescription in abiDefinition.Events.Values)
            {
                ChangeParameters(eventDescription.Inputs, abiTypesToReplace);
            }
        }
    }
}
