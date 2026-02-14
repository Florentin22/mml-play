using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;
using File = System.IO.File;

namespace update.Utils
{
    internal class GitHubReleaseService
    {
        public static HttpClient Client;
        public static string Repository = "Florentin22/mml-play";

        static GitHubReleaseService()
        {
            Client = new HttpClient();
            Client.DefaultRequestHeaders.Add("User-Agent", "updater");
        }

        public static async Task Install(ReleaseInfo releaseInfo, ReleaseService releasesService)
        {
            for (int i = 0; i < releaseInfo.AssetUrls.Count; i++)
            {
                var url = releaseInfo.AssetUrls[i];
                var name = (i < releaseInfo.AssetNames.Count) ? releaseInfo.AssetNames[i] : System.IO.Path.GetFileName(new Uri(url).LocalPath);
                var size = (i < releaseInfo.AssetSizes.Count) ? releaseInfo.AssetSizes[i] : -1;
                var label = $"{i + 1}/{releaseInfo.AssetUrls.Count}";
                await DownloadAndInstallFileAsync(url, name, size, releaseInfo.TagName, releaseInfo.releaseKind, releasesService);
            }
        }

        public static async Task DownloadAndInstallFileAsync(string url, string name, long size, string tag, ReleaseKind releaseKind, ReleaseService releasesService)
        {
            var fileName = name ?? System.IO.Path.GetFileName(new Uri(url).LocalPath);
            if (!releasesService.AppPath.DirectoryExists) Directory.CreateDirectory(releasesService.AppPath.DirectoryPath);
            var targetFile = System.IO.Path.Combine(releasesService.AppPath.DirectoryPath, fileName);
            var tempDownload = targetFile + ".download";

            using (var resp = await GitHubReleaseService.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var contentLength = resp.Content.Headers.ContentLength ?? size;
                using var networkStream = await resp.Content.ReadAsStreamAsync();
                using var fileStream = new System.IO.FileStream(tempDownload, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await networkStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                }
            }

            try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { }
            System.IO.File.Move(tempDownload, targetFile);

            if (targetFile.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => ExtractZipFlatten(targetFile, releasesService.AppPath.DirectoryPath));
                try { File.Delete(targetFile); } catch { }
            }
            else if (targetFile.EndsWith(".tar.gz", System.StringComparison.OrdinalIgnoreCase) || targetFile.EndsWith(".tgz", System.StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(targetFile); } catch { }
            }
        }

        public static void ExtractZipFlatten(string zipPath, string destDir)
        {
            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var dest = System.IO.Path.Combine(destDir, entry.Name);
                    try
                    {
                        if (File.Exists(dest)) File.Delete(dest);
                        entry.ExtractToFile(dest, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static async Task<List<ReleaseInfo>> RefreshReleases()
        {
            var uri = $"https://api.github.com/repos/{Repository}/releases";
            var json = await Client.GetStringAsync(uri);

            var doc = JsonDocument.Parse(json);

            var tmp = new List<ReleaseInfo>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var tag = item.GetProperty("tag_name").GetString();
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : tag;
                var prerelease = item.GetProperty("prerelease").GetBoolean();

                var rel = new ReleaseInfo
                {
                    TagName = tag,
                    Name = name,
                    Prerelease = prerelease
                };

                if (item.TryGetProperty("published_at", out var pub) &&
                    pub.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(pub.GetString(), out var dto))
                {
                    rel.PublishedAt = dto;
                }

                if (item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                {
                    rel.Body = body.GetString();
                }

                if (item.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        rel.AssetUrls.Add(asset.GetProperty("browser_download_url").GetString());
                        rel.AssetNames.Add(asset.GetProperty("name").GetString());
                        rel.AssetSizes.Add(asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
                    }
                }
                else
                {
                    if (item.TryGetProperty("zipball_url", out var zb))
                    {
                        rel.AssetUrls.Add(zb.GetString());
                        rel.AssetNames.Add(tag + ".zip");
                        rel.AssetSizes.Add(0);
                    }
                    else if (item.TryGetProperty("tarball_url", out var tb))
                    {
                        rel.AssetUrls.Add(tb.GetString());
                        rel.AssetNames.Add(tag + ".tar.gz");
                        rel.AssetSizes.Add(0);
                    }
                }
                tmp.Add(rel);
            }
            tmp.Sort((a, b) =>
            {
                int versionCompare = ReleaseService.CompareVersions(a.Version, b.Version);
                if (versionCompare != 0)
                    return versionCompare;

                if (a.PublishedAt.HasValue && b.PublishedAt.HasValue)
                    return b.PublishedAt.Value.CompareTo(a.PublishedAt.Value);

                return 0;
            });
            return tmp;
        }
    }
}
