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

namespace Nethermind.Config
{
    public class ConfigPropertyAttribute : Attribute
    {
        public string Category { get; }
        public string Name { get; }
        public string Description { get; }

        public ConfigPropertyAttribute(string category, string name)
            : this(category, name, $"{category}.{name}")
        {
        }

        public ConfigPropertyAttribute(string category, string name, string description)
        {
            Category = category;
            Name = name;
            Description = description;
        }
    }
}