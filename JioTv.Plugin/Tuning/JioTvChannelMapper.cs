using System;
using System.Collections.Generic;
using JioTv.Plugin.Network;

namespace JioTv.Plugin.Tuning;

/// <summary>Maps JioTV channels to Jellyfin ChannelInfo rows. Pure logic, unit-tested.</summary>
public static class JioTvChannelMapper
{
    /// <summary>
    /// Converts the Jio channel list into Jellyfin ChannelInfo entries.
    /// Channel Id is the numeric Jio channel id (used by playback + heal code),
    /// Number uses the same id so guide ordering is stable.
    /// </summary>
    public static IEnumerable<MediaBrowser.Controller.LiveTv.ChannelInfo> Map(IEnumerable<Channel> channels, string tunerHostId)
    {
        var result = new List<MediaBrowser.Controller.LiveTv.ChannelInfo>();
        int displayNumber = 1;
        foreach (var channel in channels)
        {
            var info = new MediaBrowser.Controller.LiveTv.ChannelInfo
            {
                Id = channel.Id,
                Number = displayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = channel.Name,
                Path = channel.Id,
                TunerHostId = tunerHostId,
                ChannelType = MediaBrowser.Model.LiveTv.ChannelType.TV,
            };
            if (!string.IsNullOrEmpty(channel.LogoUrl))
            {
                info.ImagePath = channel.LogoUrl.Contains('/', StringComparison.Ordinal) && channel.LogoUrl.Contains(':', StringComparison.Ordinal)
                    ? channel.LogoUrl
                    : string.Concat(Network.JioConstants.LogoBaseUrl, channel.LogoUrl);
            }

            result.Add(info);
            displayNumber++;
        }

        return result;
    }
}
