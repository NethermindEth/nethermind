using System;
using LightInject;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Runner
{
    public class LightInjectServiceProvider : IServiceProvider
    {
        private readonly ServiceContainer _serviceContainer;

        public LightInjectServiceProvider(ServiceContainer serviceContainer)
        {
            _serviceContainer = serviceContainer;
        }

        public void Build(IServiceCollection services)
        {
            foreach (var service in services)
            {
                if (service.ImplementationInstance != null)
                {
                    _serviceContainer.RegisterInstance(service.ServiceType, service.ImplementationInstance);
                }
                else if (service.ImplementationFactory != null)
                {
                    _serviceContainer.Register(factory => service.ImplementationFactory.Invoke(this), GetLifeType(service.Lifetime));
                }
                else
                {
                    _serviceContainer.Register(service.ServiceType, service.ImplementationType, GetLifeType(service.Lifetime));
                }
            }
        }

        private ILifetime GetLifeType(ServiceLifetime serviceLifetime)
        {
            switch (serviceLifetime)
            {
                case ServiceLifetime.Singleton:
                    return new PerContainerLifetime();
                case ServiceLifetime.Scoped:
                    return new PerScopeLifetime();
                case ServiceLifetime.Transient:
                    return new PerRequestLifeTime();
                default:
                    throw new Exception($"Unsupported service lifeType: {serviceLifetime}");
            }
        }

        public object GetService(Type serviceType)
        {
            return _serviceContainer.GetInstance(serviceType);
        }
    }
}