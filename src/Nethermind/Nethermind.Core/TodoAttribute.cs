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

namespace Nethermind.Core
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class TodoAttribute : Attribute
    {
        private readonly string _issueLink;
        private readonly string _comment;
        private readonly Impr _impr;

        public TodoAttribute(string comment)
        {
            _comment = comment;
        }
        
        public TodoAttribute(Impr impr, string issueLink = null)
        {
            _impr = impr;
            _issueLink = issueLink;
        }
    }

    [Flags]
    public enum Impr
    {
        None,
        Allocations,
        MemoryUsage,
        Performance,
        Readability,
        TestCoverage,
        All
    }
}