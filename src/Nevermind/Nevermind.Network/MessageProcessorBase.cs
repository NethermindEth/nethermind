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

using System.Collections.Generic;

namespace Nevermind.Network
{
    // TODO: in-place processors?
    public abstract class MessageProcessorBase<TLeft, TRight> : IMessageProcessor
    {
        void IMessageProcessor.ToRightBase(object left, IList<object> output)
        {
            IList<TRight> typedOutput = new List<TRight>();
            ToRight((TLeft)left, typedOutput);
            foreach (TRight right in typedOutput)
            {
                output.Add(right);
            }
        }

        void IMessageProcessor.ToLeftBase(object right, IList<object> output)
        {
            IList<TLeft> typedOutput = new List<TLeft>();
            ToLeft((TRight)right, typedOutput);
            foreach (TLeft left in typedOutput)
            {
                output.Add(left);
            }
        }

        public abstract void ToRight(TLeft input, IList<TRight> output);

        public abstract void ToLeft(TRight input, IList<TLeft> output);
    }
}