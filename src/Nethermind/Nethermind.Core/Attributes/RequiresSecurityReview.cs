// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property)]
    public class RequiresSecurityReviewAttribute : Attribute
    {
        private readonly string _comment;

        public RequiresSecurityReviewAttribute(string comment)
        {
            _comment = comment;
        }
    }
}
