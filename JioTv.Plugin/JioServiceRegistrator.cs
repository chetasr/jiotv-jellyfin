using System;
using JioTv.Plugin.Auth;
using JioTv.Plugin.Network;
using JioTv.Plugin.Streaming;
using JioTv.Plugin.Tuning;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;

#pragma warning disable CS1591

namespace JioTv.Plugin;

/// <summary>
/// Registers the plugin's singleton streaming services (cipher, token cache,
/// proxy fetcher, renderer) and the proxy controller endpoints with Jellyfin.
/// ITunerHost implementations in plugin assemblies are discovered
/// automatically by Jellyfin's ITunerHostManager — no manual registration.
/// </summary>
public sealed class JioServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var key = SecureKeyStore.LoadOrCreateKey(
                System.IO.Path.Combine(appPaths.PluginConfigurationsPath, "JioTv"));
            return SecureUrlCipher.CreateWithKey(key);
        });

        serviceCollection.AddSingleton<HdneaCache>();
        serviceCollection.AddSingleton<IJioProxyFetcher, JioProxyFetcher>();
        serviceCollection.AddSingleton<CredentialStore>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            return new CredentialStore(
                System.IO.Path.Combine(appPaths.PluginConfigurationsPath, "JioTv"));
        });
        serviceCollection.AddSingleton<JioAuth>();

        // Request-time client bound to credentials snapshot at construction;
        // the stream/channel sources reload credentials per call in v2.
        serviceCollection.AddSingleton<JioTvClient>(sp => new JioTvClient(
            sp.GetRequiredService<CredentialStore>().Load() ?? new JioCredentials()));

        serviceCollection.AddSingleton<IUpstream>(sp => new JioUpstreamAdapter(
            sp.GetRequiredService<IJioProxyFetcher>(),
            () => sp.GetRequiredService<JioTvClient>()));

        serviceCollection.AddSingleton<ManifestRewriter>(sp => new ManifestRewriter(
            sp.GetRequiredService<ISecureUrlCipher>(),
            "/JioTv"));

        serviceCollection.AddSingleton<HlsProxyRenderer>();
        serviceCollection.AddTransient<JioTvProxyController>();

        serviceCollection.AddSingleton<IJioChannels, JioTvChannelSource>();
        serviceCollection.AddSingleton<IJioStreams, JioTvStreamSource>();
    }
}

/// <summary>Loads/persists the AES key used to mint proxy 'auth=' tokens.</summary>
public static class SecureKeyStore
{
    /// <summary>
    /// Reads the stored 32-byte key or creates one; the key lives in the
    /// plugin data directory. Rotating it invalidates old URLs safely — the
    /// player re-fetches the manifest and gets fresh 'auth' params.
    /// </summary>
    public static byte[] LoadOrCreateKey(string pluginDataDirectory)
    {
        var path = System.IO.Path.Combine(pluginDataDirectory, "secure_url_key.bin");
        if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length == 32)
        {
            return System.IO.File.ReadAllBytes(path);
        }

        var key = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(key);
        System.IO.Directory.CreateDirectory(pluginDataDirectory);
        System.IO.File.WriteAllBytes(path, key);
        return key;
    }
}
