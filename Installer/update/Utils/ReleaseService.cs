using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace update.Utils
{
    public enum ReleaseChannel
    {
        ReleaseOnly,
        PrereleaseOnly,
        All
    }

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
        public Version Version => ReleaseService.ParseVersion(TagName);
        public ReleaseKind releaseKind => TagName.EndsWith("-installer") ? ReleaseKind.Installer : ReleaseKind.App;
        public List<string> AssetUrls { get; set; } = new();
        public List<string> AssetNames { get; set; } = new();
        public List<long> AssetSizes { get; set; } = new();
    }
    public class FilePath
    {
        public string? DirectoryPath { get; set; }
        public string? FileName { get; set; }
        public string? FullPath => DirectoryPath != null && FileName != null ? Path.Combine(DirectoryPath, FileName): null;
        public bool DirectoryExists => DirectoryPath != null && Directory.Exists(DirectoryPath);
        public bool FileExists => FullPath != null && File.Exists(FullPath);
        public FilePath(string directoryPath, string fileName)
        {
            DirectoryPath = directoryPath;
            FileName = fileName;
        }
        public FilePath(string fullPath)
        {
            DirectoryPath = Path.GetDirectoryName(fullPath);
            FileName = Path.GetFileName(fullPath);
        }
        public void FilePathWrite(string fullPath)
        {
            DirectoryPath = Path.GetDirectoryName(fullPath);
            FileName = Path.GetFileName(fullPath);
        }
    }
    internal class ReleaseService
    {
        public List<ReleaseInfo> releases = new List<ReleaseInfo>();
        public FilePath AppPath { get; set; }
        public FilePath InstallerPath { get; set; }
        public Version? AppVersion => AppPath?.FullPath != null ? GetVersion(AppPath.FullPath) : null;
        public Version? InstallerVersion => InstallerPath?.FullPath != null ? GetVersion(InstallerPath.FullPath): null;
        public ReleaseInfo? GetAnyUpdate()
        {
            return GetFilteredRelease(ReleaseKind.App, ReleaseChannel.All, AppVersion);
        }
        public ReleaseInfo? GetPrerelease()
        {
            return GetFilteredRelease(ReleaseKind.App, ReleaseChannel.PrereleaseOnly, AppVersion);
        }

        public ReleaseInfo? GetRelease()
        {
            return GetFilteredRelease(ReleaseKind.App, ReleaseChannel.ReleaseOnly, AppVersion);
        }
        public ReleaseInfo? GetLatestRelease()
        {
            return GetFilteredRelease(ReleaseKind.App, ReleaseChannel.ReleaseOnly, null);
        }

        public ReleaseInfo? GetLatestInstaller()
        {
            return GetFilteredRelease(ReleaseKind.Installer, ReleaseChannel.ReleaseOnly, InstallerVersion);
        }

        public ReleaseInfo? GetFilteredRelease(
            ReleaseKind kind,
            ReleaseChannel channel,
            Version? minVersion)
        {
            return releases
                .Where(r => r.releaseKind == kind)
                .Where(r =>
                    channel switch
                    {
                        ReleaseChannel.ReleaseOnly => !r.Prerelease,
                        ReleaseChannel.PrereleaseOnly => r.Prerelease,
                        ReleaseChannel.All => true,
                        _ => true
                    })
                .Where(r => minVersion == null || IsGreaterThan(r.Version, minVersion))
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();
        }
        public static Version? GetVersion(string path)
        {
            if (!(new FilePath(path)).FileExists) return null;
            return ParseVersion((FileVersionInfo.GetVersionInfo(path)).FileVersion);
        }
        public static Version ParseVersion(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var text = s.Trim();
                if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
                var idx = text.IndexOfAny(new[] { '-', '_' });
                string basePart = idx >= 0 ? text.Substring(0, idx) : text;
                string suffix = idx >= 0 ? text.Substring(idx + 1) : string.Empty;

                var parts = basePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
                int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
                int minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
                int build = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
                int revision = 0;

                if (parts.Length > 3 && int.TryParse(parts[3], out var r4)) revision = r4;

                if (!string.IsNullOrEmpty(suffix))
                {
                    var betaMatch = Regex.Match(suffix, "beta[\\._-]?(\\d+)", RegexOptions.IgnoreCase);
                    if (betaMatch.Success && int.TryParse(betaMatch.Groups[1].Value, out var bn))
                    {
                        revision = bn;
                    }
                }

                return new Version(major, minor, build, revision);
            }
            catch
            {
                return null;
            }
        }

        public static string FormatVersionAsTag(Version? v)
        {
            if (v == null) return null;

            var major = v.Major;
            var minor = v.Minor;
            var build = v.Build >= 0 ? v.Build : 0;
            var rev = v.Revision >= 0 ? v.Revision : 0;

            if (rev > 0)
            {
                return $"v{major}.{minor}.{build}-beta.{rev}";
            }

            return $"v{major}.{minor}.{build}";
        }

        public static bool IsPrerelease(Version? v)
        {
            return v != null && v.Revision > 0;
        }

        public static bool IsGreaterThan(Version? v1, Version? v2)
        {
            if (v1 == null) return false;
            if (v2 == null) return true;

            var base1 = new Version(v1.Major, v1.Minor, Math.Max(0, v1.Build));
            var base2 = new Version(v2.Major, v2.Minor, Math.Max(0, v2.Build));

            if (base1 != base2)
                return base1 > base2;

            bool p1 = IsPrerelease(v1);
            bool p2 = IsPrerelease(v2);

            if (p1 != p2)
                return !p1;

            int r1 = Math.Max(0, v1.Revision);
            int r2 = Math.Max(0, v2.Revision);

            return r1 > r2;
        }

        public static int CompareVersions(Version? v1, Version? v2)
        {
            if (IsGreaterThan(v1, v2)) return -1;
            if (IsGreaterThan(v2, v1)) return 1;
            return 0;
        }

    }
}
