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

namespace Nethermind.Core.Attributes
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class TodoAttribute : Attribute
    {
        private readonly string? _issueLink;
        private readonly string _comment;
        private readonly Improve _improve;

        public TodoAttribute(string comment)
        {
            _comment = comment;
        }
        
        public TodoAttribute(Improve improve, string comment, string issueLink = MissingIssueLinkMessage)
        {
            _improve = improve;
            _issueLink = issueLink;
            _comment = comment;
        }

        public const string MissingIssueLinkMessage = "No issue created or link missing";
    }

    [Flags]
    public enum Improve
    {
        None = 0,
        Allocations = 1,
        MemoryUsage = 2,
        Performance = 4,
        Readability = 8,
        TestCoverage = 16,
        Refactor = 32,
        MissingFunctionality = 64,
        Documentation = 128,
        Security = 256,
        Review = 512,
        All = 1023
    }
}
