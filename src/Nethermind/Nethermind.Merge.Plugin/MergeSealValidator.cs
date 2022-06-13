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

using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin;

public class MergeSealValidator : ISealValidator
{
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly ISealValidator _preMergeSealValidator;

    public MergeSealValidator(
        IPoSSwitcher poSSwitcher,
        ISealValidator preMergeSealValidator)
    {
        _poSSwitcher = poSSwitcher;
        _preMergeSealValidator = preMergeSealValidator;
    }
    public bool ValidateParams(BlockHeader parent, BlockHeader header)
    {
        if (_poSSwitcher.IsPostMerge(header))
        {
            return true;
        }
            
        return _preMergeSealValidator.ValidateParams(parent, header);
    }

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        if (_poSSwitcher.IsPostMerge(header))
        {
            return true;
        }

        return _preMergeSealValidator.ValidateSeal(header, force);
    }
}
