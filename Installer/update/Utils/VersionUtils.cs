using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace update
{
    static class VersionUtils
    {

        public static Version? GetVersion(string path)
        {
            return VersionUtils.ParseVersion((FileVersionInfo.GetVersionInfo(path)).FileVersion);
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

        public static string FormatVersionAsTag(Version v)
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

        public static bool IsPrerelease(Version v)
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
    }
}
