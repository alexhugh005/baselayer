using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
namespace BaseLayer.Api.Providers;

public static class BatteryRegistration
{
    public static IServiceCollection AddBatteryServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SimulatedBatteryOptions>()
            .Bind(configuration.GetSection(SimulatedBatteryOptions.SectionName))
            .Validate(options => options.IsValid(), "Battery simulation requires finite values, positive capacity, and an initial charge between 0 and 100.")
            .ValidateOnStart();
        services.AddScoped<IBatteryService, BatteryService>();
        // Replace this registration with a Core API adapter when that dependency exists.
        services.AddSingleton<IBatteryProvider, SimulatedBatteryProvider>();
        return services;
    }
}
