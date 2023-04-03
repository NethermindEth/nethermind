// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

namespace Nethermind.GitBook
{
    public class MethodData
    {
        public bool? IsImplemented { get; set; }

        public Type ReturnType { get; set; }

        public ParameterInfo[] Parameters { get; set; }

        public string Description { get; set; }

        public string EdgeCaseHint { get; set; }

        public string ResponseDescription { get; set; }

        public string ExampleResponse { get; set; }

        public bool IsFunction { get; set; }

        public InvocationType InvocationType { get; set; }
    }
}
