using System;

namespace Nevermind.Core.Validators
{
    public class OmmersValidator
    {
        public static bool ValidateOmmers(Block block)
        {
            if (block.Ommers.Length > 2)
            {
                return false;
            }

            throw new NotImplementedException();
        }
    }
}