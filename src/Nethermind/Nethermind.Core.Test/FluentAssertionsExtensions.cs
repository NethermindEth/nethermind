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

using System.Collections.Generic;
using FluentAssertions;
using FluentAssertions.Collections;

namespace Nethermind.Core.Test
{
    public static class FluentAssertionsExtensions
    {
        /// <summary>
        /// Asserts that a collection of objects is equivalent to another collection of objects.
        /// </summary>
        /// <remarks>
        /// Objects within the collections are equivalent when both object graphs have equally named properties with the same
        /// value, irrespective of the type of those objects. Two properties are also equal if one type can be converted to another
        /// and the result is equal.
        /// The type of a collection property is ignored as long as the collection implements <see cref="IEnumerable{T}"/> and all
        /// items in the collection are structurally equal.
        /// Notice that actual behavior is determined by the global defaults managed by <see cref="AssertionOptions"/>.
        /// </remarks>
        /// <param name="assertions">Assertion</param>
        /// <param name="expectation">An <see cref="IEnumerable{T}"/> with the expected elements.</param>
        /// <param name="because">
        /// A formatted phrase as is supported by <see cref="string.Format(string,object[])" /> explaining why the assertion
        /// is needed. If the phrase does not start with the word <i>because</i>, it is prepended automatically.
        /// </param>
        /// <param name="becauseArgs">
        /// Zero or more objects to format using the placeholders in <paramref name="because"/>.
        /// </param>
        public static AndConstraint<TAssertions> BeEquivalentTo<TCollection, T, TAssertions, TExpectation>(
            this GenericCollectionAssertions<TCollection, T, TAssertions> assertions, 
            TExpectation expectation,
            string because = "", 
            params object[] becauseArgs) 
            where TCollection : IEnumerable<T> 
            where TAssertions : GenericCollectionAssertions<TCollection, T, TAssertions> =>
            assertions.BeEquivalentTo(new[] { expectation }, because, becauseArgs);
        
        /// <summary>
        /// Asserts that a collection of objects is equivalent to another collection of objects.
        /// </summary>
        /// <remarks>
        /// Objects within the collections are equivalent when both object graphs have equally named properties with the same
        /// value, irrespective of the type of those objects. Two properties are also equal if one type can be converted to another
        /// and the result is equal.
        /// The type of a collection property is ignored as long as the collection implements <see cref="IEnumerable{T}"/> and all
        /// items in the collection are structurally equal.
        /// Notice that actual behavior is determined by the global defaults managed by <see cref="AssertionOptions"/>.
        /// </remarks>
        /// <param name="assertions">Assertion</param>
        /// <param name="expectation">An <see cref="IEnumerable{T}"/> with the expected elements.</param>
        public static AndConstraint<TAssertions> BeEquivalentTo<TCollection, T, TAssertions, TExpectation>(this GenericCollectionAssertions<TCollection, T, TAssertions> assertions, params TExpectation[] expectation) 
            where TCollection : IEnumerable<T> 
            where TAssertions : GenericCollectionAssertions<TCollection, T, TAssertions> =>
            assertions.BeEquivalentTo(expectation);
    }
}
