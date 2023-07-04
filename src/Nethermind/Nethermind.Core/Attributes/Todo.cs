// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
