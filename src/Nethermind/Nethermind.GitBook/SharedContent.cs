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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.GitBook.Extensions;

namespace Nethermind.GitBook
{
    public class SharedContent
    {
        private string ReplaceType(Type type)
        {
            if (type.Name.Contains("`")) return "Array";
            
            string replacedType = type.Name switch
            {
                "Address" => "Address",
                "BigInteger" => "Quantity",
                "Bloom" => "Bloom Object",
                "Boolean" => "Boolean",
                "Byte" => "Data",
                "Byte[]" => "Data",
                "Byte[][]" => "Data",
                "Decimal" => "Quantity",
                "Int32" => "Quantity",
                "Int32[]" => "Array",
                "Int64" => "Quantity",
                "Int64&" => "Quantity",
                "JsValue" => "JavaScript Object",
                "Keccak" => "Hash",
                "Object" => "Object",
                "Object[]" => "Array",
                "String" => "String",
                "String[]" => "Array",
                "UInt64" => "Quantity",
                "UInt256" => "Quantity",
                "UInt256[]" => "Array",
                _ => $"{type.Name} object",
            };
            return replacedType;
        }
        
        public void AddObjectsDescription(StringBuilder moduleBuilder, List<Type> typesToDescribe)
        {
            foreach (Type type in typesToDescribe.Distinct().Where(type => ReplaceType(type).Contains("object")))
            {
                moduleBuilder.AppendLine();
                moduleBuilder.AppendLine(@$"`{type.Name}`");
                moduleBuilder.AppendLine();

                if(type == typeof(BlockParameterType))
                {
                    moduleBuilder.AppendLine("- `Quantity` or `String` (latest, earliest, pending)");
                    moduleBuilder.AppendLine();
                    continue;
                }
                
                if(type == typeof(TxType))
                {
                    moduleBuilder.AppendLine("- [EIP2718](https://eips.ethereum.org/EIPS/eip-2718) transaction type");
                    moduleBuilder.AppendLine();
                    continue;
                }
                
                Type typeToDescribe = type.IsArray ? type.GetElementType() : type;

                PropertyInfo[] properties = typeToDescribe.GetProperties();

                moduleBuilder.AppendLine("| Field name | Type |");
                moduleBuilder.AppendLine("| :--- | :--- |");

                string propertyType;
                foreach (PropertyInfo property in properties)
                {
                    propertyType = GetTypeToWrite(property.PropertyType, null);
                    moduleBuilder.AppendLine($"| {property.Name} | `{propertyType}` |");
                }
            }
        }
        
        public string GetTypeToWrite(Type type, List<Type> typesToDescribe)
        {
            if (type.IsNullable())
            {
                type = Nullable.GetUnderlyingType(type);
            }

            string replacedType = ReplaceType(type);

            if (replacedType.Equals($"{type.Name} object") && typesToDescribe != null)
            {
                typesToDescribe.Add(type);
                AdditionalPropertiesToDescribe(type, typesToDescribe);
            }

            return replacedType;
        }
        
        private void AdditionalPropertiesToDescribe(Type type, List<Type> typesToDescribe)
        {
            PropertyInfo[] properties = type.GetProperties()
                .Where(p => !p.PropertyType.IsPrimitive
                            && p.PropertyType != typeof(string) 
                            && p.PropertyType != typeof(long)
                            && p.PropertyType != typeof(Keccak))
                .ToArray();

            foreach (PropertyInfo property in properties)
            {
                if (property.PropertyType.IsNullable())
                {
                    Type underlyingType = Nullable.GetUnderlyingType(property.PropertyType);
                    typesToDescribe.Add(underlyingType);
                }
                else
                {
                    typesToDescribe.Add(property.PropertyType);
                }
            }
        }

        public void Save(string moduleName, string docsDir, StringBuilder docBuilder)
        {
            string moduleFile = $"{docsDir}/{moduleName}.md";
            string fileContent = docBuilder.ToString();
            File.WriteAllText(moduleFile, fileContent);
        }
    }
}
