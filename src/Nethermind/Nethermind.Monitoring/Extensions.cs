using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Monitoring.Metrics;
using Prometheus;

namespace Nethermind.Monitoring
{
    public static class Extensions
    {
        public static void AddMonitoring(this IServiceCollection services)
        {
            services.AddHostedService<MetricsHostedService>();
            services.AddSingleton<IMetricsUpdater, MetricsUpdater>();
        }
        
        public static void UseMonitoring(this IApplicationBuilder app)
        {
            app.UseMetricServer();
        }
    }
}