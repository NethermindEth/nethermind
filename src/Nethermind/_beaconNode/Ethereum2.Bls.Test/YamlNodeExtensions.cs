/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace Ethereum2.Bls.Test
{
    public static class YamlNodeExtensions
    {
        public static T?[]? ArrayProp<T>(this YamlNode yamlNode, string propertyName) where T : class
        {
            YamlMappingNode? mappingNode = yamlNode as YamlMappingNode;
            IEnumerable<string?>? result =
                (mappingNode?[propertyName] as YamlSequenceNode)?.Children.Select(i => (i as YamlScalarNode)?.Value);
            return result?.Select(i => i is null ? null : (T) Convert.ChangeType(i, typeof(T))).ToArray();
        }

        public static T?[]? ArrayProp<T>(this YamlNode yamlNode, string propertyName,
            Func<YamlSequenceNode?, T> converter) where T : class
        {
            YamlMappingNode? mappingNode = yamlNode as YamlMappingNode;
            T[]? result = (mappingNode?[propertyName] as YamlSequenceNode)?.Children
                .Select(i => converter(i as YamlSequenceNode)).ToArray();
            return result;
        }

        public static T? Prop<T>(this YamlNode yamlNode, string propertyName) where T : class
        {
            YamlMappingNode? mappingNode = yamlNode as YamlMappingNode;
            if (mappingNode is null)
            {
                return null;
            }

            return (T?) Convert.ChangeType((mappingNode[propertyName] as YamlScalarNode)?.Value, typeof(T));
        }
    }
}