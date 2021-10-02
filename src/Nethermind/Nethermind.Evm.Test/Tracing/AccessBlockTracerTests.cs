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
// 

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Evm.Tracing.Access;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Blockchain.Tracing;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class AccessBlockTracerTests
    {
        [Test]
        public void Starts_with_empty_addresses_accessed()
        {
            AccessBlockTracer blockTracer = new(Array.Empty<Address>());
            blockTracer.BuildResult().Should().BeNullOrEmpty();
        }

        [Test]
        public void Can_trace_correctly()
        {
            
        }
    }
}
