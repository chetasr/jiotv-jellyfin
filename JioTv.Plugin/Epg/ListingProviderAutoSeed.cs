using System;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.LiveTv;

#pragma warning disable CS1591

namespace JioTv.Plugin.Epg;

/// <summary>
/// Seeds Jellyfin's LiveTv options with our own listing provider of type
/// "jiotv" once, so the EPG activates automatically without the user
/// manually adding a listings provider in the dashboard.
/// </summary>
public sealed class ListingProviderAutoSeed
{
    private readonly IConfigurationManager _configurationManager;

    public ListingProviderAutoSeed(IConfigurationManager configurationManager)
    {
        _configurationManager = configurationManager;
    }

    public void TrySeedListingProvider()
    {
        try
        {
            var liveTv = _configurationManager.GetConfiguration<LiveTvOptions>("livetv");

            var hasListing = liveTv.ListingProviders is not null
                && liveTv.ListingProviders.Any(lp =>
                    string.Equals(lp.Type, "jiotv", StringComparison.OrdinalIgnoreCase));

            if (hasListing)
            {
                return;
            }

            var info = new ListingsProviderInfo
            {
                Id = "jiotv-listings",
                Type = "jiotv",
                EnableAllTuners = true,
            };

            var allProviders = liveTv.ListingProviders is null
                ? new[] { info }
                : liveTv.ListingProviders.Append(info).ToArray();

            liveTv.ListingProviders = allProviders;
            _configurationManager.SaveConfiguration("livetv", liveTv);
        }
        catch
        {
            // Seeding is best-effort; failing here must not break the tuner itself.
        }
    }
}
