using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using update.Models;
namespace update
{
    public partial class MainWindow : Window
    {
        ObservableCollection<Models.ReleaseInfo> releases = new();
        readonly Queue<DateTime> refreshTimestamps = new();
        HttpClient http;
        Version installedVersionCache = null;
        bool isInstalledCache = false;
        string installPath = string.Empty;
        string ExePath = Environment.ProcessPath!;
        string[] args = Environment.GetCommandLineArgs();
        string DirPath;
        const string ProgramExeName = "MML Play.exe";
        string repo = "Florentin22/mml-play";

        public MainWindow()
        {
            InitializeComponent();
            ReleasesListBox.ItemsSource = releases;
            try { ReleasesListBox.SelectionChanged += ReleasesListBox_SelectionChanged; } catch { }
            http = new HttpClient();
            try { http.DefaultRequestHeaders.Add("User-Agent", "updater"); } catch { }
            ApplySystemLanguage();
            DirPath = Path.GetDirectoryName(ExePath) ?? "";
            installPath = DirPath;
            InstallPathText.Text = installPath;
            CheckVersion();
            try { IncludePrereleaseCheck.IsChecked = (installedVersionCache != null && VersionUtils.IsPrerelease(installedVersionCache)); } catch { }
            StatusText.Text = GetString("Wait");
            SetupCollectionViewFilter();
            _ = Task.Run(async () =>
            {
                await RefreshReleasesAsync();
                await HandleArgsAsync(args);
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = GetString("Ready");
                });
                await UpdateIntaller();
            });
        }

        private async Task UpdateIntaller()
        {
            try
            {
                var installer = GetLatestInstaller();
                if (installer == null) return;
                if (!VersionUtils.IsGreaterThan(installer.Version, VersionUtils.GetVersion(ExePath)))
                    return;
                await Install(installer);

                var fileName = installer.AssetNames.FirstOrDefault();
                var path = Path.Combine(installPath, fileName);
                string exePath = ExePath;

                CreateAndRunUpdaterBatch(exePath, path);

                await Dispatcher.InvokeAsync(() => { System.Windows.Application.Current.Shutdown(); });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"{GetString("Error")}: {ex.Message}";
            }
        }

        public void CreateAndRunUpdaterBatch(string currentInstallerPath, string downloadedArchivePath)
        {
            string tempBatchPath = Path.Combine(Path.GetTempPath(), "update_installer.bat");
            string argsString = string.Join(" ", args.Skip(1).Select(a => $"\"{a.Replace("\"", "\\\"")}\""));

            string batchContent = $@"
            @echo off
            :waitLoop
            tasklist /FI ""IMAGENAME eq {Path.GetFileName(currentInstallerPath)}"" | find /I ""{Path.GetFileName(currentInstallerPath)}"" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto waitLoop
            )
            rem удаляем старый инсталлер
            del /f /q ""{currentInstallerPath}""
            rem распаковываем архив в папку инсталлятора
            powershell -Command ""Expand-Archive -Force '{downloadedArchivePath}' '{Path.GetDirectoryName(currentInstallerPath)}'""
            rem удаляем архив обновления
            del /f /q ""{downloadedArchivePath}""
            rem запускаем новый инсталлятор
            start """" ""{currentInstallerPath}"" {argsString}
            rem удаляем сам батник
            del /f /q ""%~f0""
            ";

            File.WriteAllText(tempBatchPath, batchContent);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = tempBatchPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }

        private void ApplySystemLanguage()
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (culture.Equals("ru", StringComparison.OrdinalIgnoreCase))
                LanguageComboBox.SelectedItem = LanguageComboBoxRu;
            else
                LanguageComboBox.SelectedItem = LanguageComboBoxEn;
        }
        async Task DownloadAndInstallFileAsync(string url, string name, long size, string tag, ReleaseKind releaseKind)
        {
            try
            {
                var fileName = name ?? System.IO.Path.GetFileName(new Uri(url).LocalPath);
                if (!Directory.Exists(installPath)) Directory.CreateDirectory(installPath);
                var targetFile = System.IO.Path.Combine(installPath, fileName);
                var tempDownload = targetFile + ".download";

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgress.Value = 0;
                    DownloadProgress.Visibility = Visibility.Visible;
                });

                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
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
                        if (contentLength > 0)
                        {
                            var percent = (double)totalRead / contentLength * 100.0;
                            await Dispatcher.InvokeAsync(() => DownloadProgress.Value = Math.Min(100, percent));
                        }
                    }
                }

                try { if (File.Exists(targetFile)) File.Delete(targetFile); } catch { }
                System.IO.File.Move(tempDownload, targetFile);
                await Dispatcher.InvokeAsync(() => StatusText.Text = GetString("DownloadedTo") + ": " + targetFile);

                if (releaseKind == ReleaseKind.App)
                    if (targetFile.EndsWith(".zip", System.StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Run(() => ExtractZipFlatten(targetFile, installPath));
                        try { File.Delete(targetFile); } catch { }
                    }
                    else if (targetFile.EndsWith(".tar.gz", System.StringComparison.OrdinalIgnoreCase) || targetFile.EndsWith(".tgz", System.StringComparison.OrdinalIgnoreCase))
                    {
                        // tarballs are not extracted by this updater; remove archive to avoid leaving temp files
                        try { File.Delete(targetFile); } catch { }
                    }
                await Dispatcher.InvokeAsync(() => StatusText.Text = GetString("Installed") + " " + tag);

            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = GetString("InstallFailed") + ": " + ex.Message);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() => DownloadProgress.Visibility = Visibility.Collapsed);
            }
        }

        async Task HandleArgsAsync(string[] args)
        {
            var installArg = args.FirstOrDefault(a => a.StartsWith("update", System.StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(installArg))
            {
                await Update();
                await RunApp();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var now = DateTime.UtcNow;
                while (refreshTimestamps.Count > 0 && (now - refreshTimestamps.Peek()).TotalSeconds > 60)
                {
                    refreshTimestamps.Dequeue();
                }

                if (refreshTimestamps.Count >= 3)
                {
                    _ = Dispatcher.InvokeAsync(() => StatusText.Text = GetString("TooManyRefreshesDetail"));
                    return;
                }

                refreshTimestamps.Enqueue(now);
            }
            catch { }

            _ = RefreshReleasesAsync();
        }

        private async Task RefreshReleasesAsync()
        {
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshButton.IsEnabled = false;
                releases.Clear();
            });
            try
            {
                bool includePrerelease = false;
                await Dispatcher.InvokeAsync(() =>
                    includePrerelease = IncludePrereleaseCheck.IsChecked.GetValueOrDefault());

                var uri = $"https://api.github.com/repos/{repo}/releases";
                var json = await http.GetStringAsync(uri);

                var doc = JsonDocument.Parse(json);

                var tmp = new List<Models.ReleaseInfo>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var tag = item.GetProperty("tag_name").GetString();
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : tag;
                    var prerelease = item.GetProperty("prerelease").GetBoolean();

                    var rel = new Models.ReleaseInfo
                    {
                        TagName = tag,
                        Name = name,
                        Prerelease = prerelease
                    };

                    rel.IsVisible = false;

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
                    try
                    {
                        var va = a.Version;
                        var vb = b.Version;
                        if (va != null && vb != null)
                        {
                            var c = vb.CompareTo(va);
                            if (c != 0) return c;
                        }

                        if (a.PublishedAt.HasValue && b.PublishedAt.HasValue)
                            return b.PublishedAt.Value.CompareTo(a.PublishedAt.Value);
                    }
                    catch { }
                    return 0;
                });

                foreach (var rel in tmp)
                {
                    await Dispatcher.InvokeAsync(() => releases.Add(rel));
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = GetString("FailedToGetReleases") + ": " + GetString("Error") + ": " + ex.Message);
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    RefreshButton.IsEnabled = true;
                    RefreshList();
                });
            }
        }

        private void SetupCollectionViewFilter()
        {
            var view = CollectionViewSource.GetDefaultView(ReleasesListBox.ItemsSource);
            if (view == null) return;
            view.Filter = obj =>
            {
                if (obj is not Models.ReleaseInfo r)
                    return false;

                if (!r.IsVisible)
                    return false;

                var q = SearchBox.Text?.Trim();
                if (string.IsNullOrEmpty(q))
                    return true;

                return (r.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                       (r.TagName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            };
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var view = CollectionViewSource.GetDefaultView(ReleasesListBox.ItemsSource);
            view?.Refresh();
        }

        private void ReleasesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateReleaseDetails();
        }

        private void UpdateReleaseDetails()
        {
            var rel = ReleasesListBox.SelectedItem as Models.ReleaseInfo;
            var title = this.FindName("ReleaseTitleText") as TextBlock;
            var tag = this.FindName("ReleaseTagText") as TextBlock;
            var pre = this.FindName("ReleasePrereleaseText") as TextBlock;
            var pub = this.FindName("ReleasePublishedText") as TextBlock;
            var bodyBox = this.FindName("ReleaseBodyTextBox") as TextBox;
            if (rel == null)
            {
                if (title != null) title.Text = string.Empty;
                if (tag != null) tag.Text = string.Empty;
                if (pre != null) pre.Text = string.Empty;
                if (pub != null) pub.Text = string.Empty;
                if (bodyBox != null) bodyBox.Text = GetString("SelectRelease");
                return;
            }
            if (title != null) title.Text = rel.Name;
            if (tag != null) tag.Text = rel.TagName;
            if (pre != null) pre.Text = rel.Prerelease ? GetString("Yes") : GetString("No");
            if (pub != null) pub.Text = rel.PublishedDisplay;
            var lang = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
            var localized = ExtractLocalizedBody(rel.Body, lang);
            if (bodyBox != null) bodyBox.Text = localized ?? rel.Body ?? string.Empty;
        }

        private string ExtractLocalizedBody(string body, string lang)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var target = "### " + lang.ToUpperInvariant();
                int start = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith(target, StringComparison.OrdinalIgnoreCase))
                    {
                        start = i + 1; break;
                    }
                }
                if (start < 0) return null;
                var sb = new StringBuilder();
                for (int i = start; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (l.TrimStart().StartsWith("### ", StringComparison.OrdinalIgnoreCase)) break;
                    sb.AppendLine(l);
                }
                return sb.ToString().Trim();
            }
            catch { }
            return null;
        }

        private void ChoosePathButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = GetString("SelectFolderTitle"),
                InitialDirectory = DirPath
            };

            if (folderDialog.ShowDialog() == true)
            {
                installPath = folderDialog.FolderName;
                InstallPathText.Text = installPath;
                CheckVersion();
            }
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            Install();
        }

        private async Task Install(ReleaseInfo selectedRelease = null)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (selectedRelease == null)
                {
                    selectedRelease = ReleasesListBox.SelectedItem as Models.ReleaseInfo;
                    if (selectedRelease == null)
                    {
                        StatusText.Text = GetString("SelectReleaseFirst");
                        return;
                    }
                    if (string.IsNullOrEmpty(installPath))
                    {
                        StatusText.Text = GetString("ChooseInstallPathFirst");
                        return;
                    }
                    if (selectedRelease.AssetUrls == null || selectedRelease.AssetUrls.Count == 0)
                    {
                        StatusText.Text = GetString("NoAssetsSelected");
                        return;
                    }
                }
                else
                {
                    ReleasesListBox.SelectedItem = selectedRelease;
                }
                InstallButton.IsEnabled = false;
                RefreshButton.IsEnabled = false;
                ReleasesListBox.IsEnabled = false;
                SearchBox.IsEnabled = false;
            });
            await Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < selectedRelease.AssetUrls.Count; i++)
                    {
                        var url = selectedRelease.AssetUrls[i];
                        var name = (i < selectedRelease.AssetNames.Count) ? selectedRelease.AssetNames[i] : System.IO.Path.GetFileName(new Uri(url).LocalPath);
                        var size = (i < selectedRelease.AssetSizes.Count) ? selectedRelease.AssetSizes[i] : -1;
                        var label = $"{i + 1}/{selectedRelease.AssetUrls.Count}";
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusText.Text = string.Format(GetString("Downloading"), name);
                            DownloadProgress.Visibility = Visibility.Visible;
                            DownloadProgress.Value = 0;
                            ProgressLabel.Text = label;
                        });
                        await DownloadAndInstallFileAsync(url, name, size, selectedRelease.TagName, selectedRelease.releaseKind);
                    }
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ReleasesListBox.SelectedItem = null;
                        InstallButton.IsEnabled = true;
                        RefreshButton.IsEnabled = true;
                        ReleasesListBox.IsEnabled = true;
                        SearchBox.IsEnabled = true;
                        DownloadProgress.Visibility = Visibility.Collapsed;
                        ProgressLabel.Text = string.Empty;
                        CheckVersion();
                    });
                }
            });
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var ci = (LanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            SetLanguage(ci);
        }

        private void SetLanguage(string ci)
        {
            if (ci == null) return;
            ResourceDictionary dict = null;
            try
            {
                dict = new ResourceDictionary { Source = new Uri($"/update;component/Resources/StringResources.{ci}.xaml", UriKind.Relative) };
            }
            catch { return; }

            var app = System.Windows.Application.Current;
            var toRemove = app.Resources.MergedDictionaries.Where(d => d.Source != null && d.Source.OriginalString.Contains("StringResources.")).ToList();
            foreach (var d in toRemove) app.Resources.MergedDictionaries.Remove(d);
            app.Resources.MergedDictionaries.Add(dict);
            UpdateReleaseDetails();
            CheckVersion();
        }

        private bool CheckVersion()
        {
            installedVersionCache = null;
            isInstalledCache = false;

            if (string.IsNullOrEmpty(installPath))
            {
                StatusText.Text = GetString("ChooseInstallPathFirst");
                UpdatePanel();
                return false;
            }

            string exe = Path.Combine(installPath, ProgramExeName);

            if (!File.Exists(exe))
            {
                UpdatePanel();
                return false;
            }

            try
            {
                installedVersionCache = VersionUtils.GetVersion(exe);
                isInstalledCache = installedVersionCache != null;
                UpdatePanel();
                return isInstalledCache;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"{GetString("Error")}: {ex.Message}";
                UpdatePanel();
                return false;
            }
        }

        void UpdatePanel()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var includePrerelease = IncludePrereleaseCheck.IsChecked.GetValueOrDefault();
                        var isInstalled = isInstalledCache;
                        var installedVersion = installedVersionCache;
                        var prereleaseInstalled = VersionUtils.IsPrerelease(installedVersion);

                        // ---- versions ----
                        // если версия актуальна → функции вернут null
                        var latestRelease = GetRelease();
                        var latestPrerelease = includePrerelease ? GetPrerelease() : null;
                        ReleaseInfo? availableUpdate = (latestRelease == null && prereleaseInstalled) ? latestPrerelease : latestRelease;
                        bool updateAvailable = availableUpdate != null;

                        // ---- Installed text ----
                        InstalledVersionText.Text =
                            isInstalled
                                ? VersionUtils.FormatVersionAsTag(installedVersion) ?? "—"
                                : GetString("InstalledNot");

                        // ---- Available RELEASE (показываем даже если не установлено) ----
                        AvailableVersionText.Text =
                            latestRelease != null
                                ? latestRelease.TagName
                                : "—";

                        // ---- Prerelease panel ----
                        if (includePrerelease && latestPrerelease != null)
                        {
                            LatestPrereleasePanel.Visibility = Visibility.Visible;
                            LatestPrereleaseText.Text = latestPrerelease.TagName;
                        }
                        else
                        {
                            LatestPrereleasePanel.Visibility = Visibility.Collapsed;
                        }

                        // ================= BUTTONS =================

                        if (isInstalled)
                        {
                            // базовые кнопки
                            RunButton.Visibility = Visibility.Visible;
                            DeleteButton.Visibility = Visibility.Visible;
                            NoSelInstallButton.Visibility = Visibility.Collapsed;

                            // UPDATE — если GetRelease() != null
                            UpdateButton.Visibility =
                                updateAvailable ? Visibility.Visible : Visibility.Collapsed;

                            // info text
                            NoSelectionInfoText.Text = updateAvailable
                                ? $"{GetString("Installed")}: {VersionUtils.FormatVersionAsTag(installedVersion)} — {availableUpdate.TagName} {GetString("Available")}"
                                : $"{GetString("Installed")}: {VersionUtils.FormatVersionAsTag(installedVersion)}";

                            // rollback (если установлен prerelease)
                            RollbackToReleaseButton.Visibility =
                                prereleaseInstalled ? Visibility.Visible : Visibility.Collapsed;

                            // switch to prerelease — если есть prerelease новее
                            SwitchToPrereleaseButton.Visibility =
                                (!prereleaseInstalled && latestPrerelease != null)
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                        }
                        else
                        {
                            // НЕ установлено
                            RunButton.Visibility = Visibility.Collapsed;
                            DeleteButton.Visibility = Visibility.Collapsed;
                            UpdateButton.Visibility = Visibility.Collapsed;
                            RollbackToReleaseButton.Visibility = Visibility.Collapsed;
                            SwitchToPrereleaseButton.Visibility = Visibility.Collapsed;

                            // INSTALL — если есть хоть что-то доступное
                            NoSelInstallButton.Visibility =
                                (latestRelease != null || latestPrerelease != null)
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;

                            NoSelectionInfoText.Text = GetString("ReadyToInstall");
                        }
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"{GetString("Error")}: {ex.Message}";
                    }
                });
            }
            catch { }
        }

        string GetString(string key)
        {
            try
            {
                var r = System.Windows.Application.Current.TryFindResource(key);
                return r?.ToString() ?? key;
            }
            catch { return key; }
        }

        void ExtractZipFlatten(string zipPath, string destDir)
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

        private void IncludePrereleaseCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void IncludePrereleaseCheck_Checked(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private void RefreshList()
        {
            var includePrerelease = IncludePrereleaseCheck.IsChecked.GetValueOrDefault();
            foreach (var r in releases)
            {
                r.IsVisible = r.releaseKind == ReleaseKind.App && (includePrerelease || !r.Prerelease);
            }
            CollectionViewSource.GetDefaultView(ReleasesListBox.ItemsSource)?.Refresh();
            UpdatePanel();
        }

        private async Task RunApp()
        {
            try
            {
                var exePath = System.IO.Path.Combine(installPath, ProgramExeName);
                ProcessStartInfo startInfo = new ProcessStartInfo(exePath);
                startInfo.WorkingDirectory = System.IO.Path.GetDirectoryName(startInfo.FileName);
                startInfo.Arguments = "updated";
                Process.Start(startInfo);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = GetString("Error") + ": " + ex.Message);
            }
        }

        private void RunApp_Click(object sender, RoutedEventArgs e)
        {
            RunApp();
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            RunApp_Click(sender, e);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = System.IO.Path.Combine(installPath, ProgramExeName);
                if (File.Exists(exe)) File.Delete(exe);
                StatusText.Text = GetString("InstalledNot");
                CheckVersion();
            }
            catch (Exception ex)
            {
                StatusText.Text = GetString("Error") + ": " + ex.Message;
            }
        }

        private void NoSelectionInstall_Click(object sender, RoutedEventArgs e)
        {
            InstallLatestRelease();
        }

        private async Task Update()
        {
            ReleaseInfo? candidate = null;
            await Dispatcher.InvokeAsync(() =>
            {
                candidate = releases.FirstOrDefault(r => IncludePrereleaseCheck.IsChecked.GetValueOrDefault() || !r.Prerelease);
                if (candidate == null)
                {
                    StatusText.Text = GetString("ReleaseNotFound");
                    return;
                }
            });
            _ = Task.Run(async () =>
            {
                await Install(candidate);
            });
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            if (VersionUtils.IsPrerelease(installedVersionCache))
                Update();
            else
                InstallLatestRelease();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            ReleasesListBox.SelectedItem = null;
        }

        private void RollbackToReleaseButton_Click(object sender, RoutedEventArgs e)
        {
            InstallLatestRelease();
        }

        private void InstallLatestRelease()
        {
            var latest = GetLatestRelease();
            if (latest == null)
            {
                StatusText.Text = GetString("ReleaseNotFound");
                return;
            }
            Install(latest);
        }

        private void SwitchToPrereleaseButton_Click(object sender, RoutedEventArgs e)
        {
            var prerelease = GetPrerelease();
            if (prerelease == null)
            {
                StatusText.Text = GetString("ReleaseNotFound");
                return;
            }
            Install(prerelease);
        }

        private ReleaseInfo? GetPrerelease()
        {
            return releases.Where(r => r.Prerelease && VersionUtils.IsGreaterThan(r.Version, installedVersionCache)).OrderByDescending(r => r.Version).FirstOrDefault();
        }

        private ReleaseInfo? GetRelease()
        {
            return releases.Where(r => !r.Prerelease && VersionUtils.IsGreaterThan(r.Version, installedVersionCache)).OrderByDescending(r => r.Version).FirstOrDefault();
        }
        private ReleaseInfo? GetLatestRelease()
        {
            return releases.Where(r => !r.Prerelease).OrderByDescending(r => r.Version).FirstOrDefault();
        }
        private ReleaseInfo? GetLatestInstaller()
        {
            return releases.Where(r => r.releaseKind == ReleaseKind.Installer).OrderByDescending(r => r.Version).FirstOrDefault();
        }
    }
}
