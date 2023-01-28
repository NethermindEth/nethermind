// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Attributes
{
    [AttributeUsage(AttributeTargets.All)]
    public class DoNotUseInSecuredContext : Attribute
    {
        private readonly string _comment;

        [Todo(Improve.Security, "In config file add a switch for secured context and if set then throw an exception whenever this attribute is loaded?")]
        public DoNotUseInSecuredContext(string comment)
        {
            _comment = comment;
        }
    }
}
