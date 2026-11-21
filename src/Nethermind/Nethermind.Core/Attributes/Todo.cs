// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class TodoAttribute : Attribute
    {
        public string? IssueLink { get; }
        public string Comment { get; }
        public Improve Improve { get; }

        public TodoAttribute(string comment) => Comment = comment;

        public TodoAttribute(Improve improve, string comment, string issueLink = MissingIssueLinkMessage)
        {
            Improve = improve;
            IssueLink = issueLink;
            Comment = comment;
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
