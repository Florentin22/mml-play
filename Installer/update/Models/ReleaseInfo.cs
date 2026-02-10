using System;
using System.Collections.Generic;

namespace update.Models
{

    public enum ReleaseKind
    {
        App,
        Installer
    }

    public class ReleaseInfo
    {
        public string TagName { get; set; }
        public string Name { get; set; }
        public bool Prerelease { get; set; }
        public bool IsVisible { get; set; } = true;
        public DateTimeOffset? PublishedAt { get; set; }
        public string PublishedDisplay => PublishedAt.HasValue ? PublishedAt.Value.ToLocalTime().ToString("g") : string.Empty;
        public string Body { get; set; }
        public string DisplayName => $"{Name} ({TagName})";
        public Version Version => VersionUtils.ParseVersion(TagName);
        public ReleaseKind releaseKind => TagName.EndsWith("-installer") ? ReleaseKind.Installer : ReleaseKind.App;
        public List<string> AssetUrls { get; set; } = new();
        public List<string> AssetNames { get; set; } = new();
        public List<long> AssetSizes { get; set; } = new();
    }
}
