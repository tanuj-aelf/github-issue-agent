using Aevatar.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Volo.Abp.AutoMapper;
using Volo.Abp.Modularity;

namespace AevatarTemplate.Silo;

public class AevatarSiloModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpAutoMapperOptions>(options => { options.AddMaps<AevatarSiloModule>(); });
        context.Services.AddHostedService<AevatarSiloHostedService>();
        context.Services.AddSerilog(_ => { },
            true, writeToProviders: true);
        context.Services.AddHttpClient();
        context.Services.AddSingleton<IEventDispatcher, DefaultEventDispatcher>();
    }
}