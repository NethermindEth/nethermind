// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing
{
    public class CompositeBlockPreprocessorStep : IBlockPreprocessorStep
    {
        private readonly LinkedList<IBlockPreprocessorStep> _recoverySteps;

        public CompositeBlockPreprocessorStep(params IBlockPreprocessorStep[] recoverySteps)
        {
            if (recoverySteps is null) throw new ArgumentNullException(nameof(recoverySteps));

            _recoverySteps = new LinkedList<IBlockPreprocessorStep>();
            for (int i = 0; i < recoverySteps.Length; i++)
            {
                _recoverySteps.AddLast(recoverySteps[i]);
            }
        }

        public void RecoverData(Block block)
        {
            foreach (IBlockPreprocessorStep recoveryStep in _recoverySteps)
            {
                recoveryStep.RecoverData(block);
            }
        }

        public void AddFirst(IBlockPreprocessorStep blockPreprocessorStep)
        {
            _recoverySteps.AddFirst(blockPreprocessorStep);
        }

        public void AddLast(IBlockPreprocessorStep blockPreprocessorStep)
        {
            _recoverySteps.AddLast(blockPreprocessorStep);
        }
    }
}
