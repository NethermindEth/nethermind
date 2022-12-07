// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            return result?.Select(i => i is null ? null : (T)Convert.ChangeType(i, typeof(T))).ToArray();
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

            return (T?)Convert.ChangeType((mappingNode[propertyName] as YamlScalarNode)?.Value, typeof(T));
        }
    }
}
