using Microsoft.Extensions.DependencyInjection;
using NexusKit.Core.Context;
using NexusKit.Core.Modules;

namespace PlayerNexusTracker.Composition;

public sealed class PlayerNexusTrackerModule : IPluginModule
{
    public void Register(IServiceCollection services, IPluginContext context)
        => services.AddServices();
}
