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
using FluentAssertions;
using FluentAssertions.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Nethermind.Trie.Test;

public static class SpanGenericsExtension
{
    public static GenericCollectionAssertions<T> Should<T>(this Span<T> value) => value.ToArray().Should();
    public static GenericCollectionAssertions<T> Should<T>(this ReadOnlySpan<T> value) => value.ToArray().Should();
    
    public static AndConstraint<GenericCollectionAssertions<T>>  BeEquivalentTo<T>(this GenericCollectionAssertions<T> value, Span<byte> expectedValue) =>
        value.BeEquivalentTo(expectedValue.ToArray());
    public static AndConstraint<GenericCollectionAssertions<T>>  BeEquivalentTo<T>(this GenericCollectionAssertions<T> value, ReadOnlySpan<byte> expectedValue) =>
        value.BeEquivalentTo(expectedValue.ToArray());

}
