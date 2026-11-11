using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace TechStore.API.Configuration;

public sealed class TrustedProxySettings
{
    // Additional, explicitly trusted immediate proxies. Never trust a caller-supplied forwarding header by itself.
    public string[] KnownProxies { get; set; } = [];
}

public static class TrustedProxyConfiguration
{
    public static IServiceCollection AddTrustedProxyForwarding(this IServiceCollection services)
    {
        services.AddOptions<TrustedProxySettings>().BindConfiguration("ReverseProxy")
            .Validate(settings => settings.KnownProxies is not null
                && settings.KnownProxies.All(address => IPAddress.TryParse(address, out _)),
                "ReverseProxy:KnownProxies must contain literal IP addresses.").ValidateOnStart();
        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<TrustedProxySettings>>((options, configured) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
            foreach (var address in configured.Value.KnownProxies)
                options.KnownProxies.Add(IPAddress.Parse(address));
            // Preserve the actual HTTPS API transport; the development Angular server itself serves HTTP.
            // Forwarded proto/host headers are deliberately not processed.
        });
        return services;
    }
}
