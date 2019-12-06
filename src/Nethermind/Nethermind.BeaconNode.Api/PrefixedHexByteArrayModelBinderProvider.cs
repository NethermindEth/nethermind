using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Nethermind.BeaconNode.Api
{
    public class PrefixedHexByteArrayModelBinderProvider : IModelBinderProvider
    {
        public IModelBinder? GetBinder(ModelBinderProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            var modelType = context.Metadata.UnderlyingOrModelType;
            if (modelType == typeof(byte[]))
            {
                return new PrefixedHexByteArrayModelBinder();
            }
            return null;
        }
    }
}
