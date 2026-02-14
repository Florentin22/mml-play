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
using System.Windows.Media.TextFormatting;
using update.Utils;
namespace update
{
    public partial class MainWindow : Window
    {
        ReleaseService releasesService = new ReleaseService();
        readonly Queue<DateTime> refreshTimestamps = new();
        string[] args = Environment.GetCommandLineArgs();
        const string ProgramExeName = "MML Play.exe";

        public MainWindow()
        {
            InitializeComponent();
            ReleasesListBox.ItemsSource = releasesService.releases;
            try { ReleasesListBox.SelectionChanged += ReleasesListBox_SelectionChanged; } catch { }
            releasesService.InstallerPath = new FilePath(Environment.ProcessPath);
            releasesService.AppPath = new FilePath(releasesService.InstallerPath.DirectoryPath, ProgramExeName);
            InstallPathText.Text = releasesService.InstallerPath.DirectoryPath;
            ApplySystemLanguage();
            CheckVersion();
            try { IncludePrereleaseCheck.IsChecked = (releasesService.AppVersion != null && ReleaseService.IsPrerelease(releasesService.AppVersion)); } catch { }
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
                var installer = releasesService.GetLatestInstaller();
                if (installer == null) return;
                await Install(installer);

                CreateAndRunUpdaterBatch(releasesService.InstallerPath.FullPath, installer.AssetNames.FirstOrDefault());

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
            del /f /q ""{currentInstallerPath}""
            powershell -Command ""Expand-Archive -Force '{downloadedArchivePath}' '{Path.GetDirectoryName(currentInstallerPath)}'""
            del /f /q ""{downloadedArchivePath}""
            start """" ""{currentInstallerPath}"" {argsString}
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
                if (!releasesService.AppPath.DirectoryExists) Directory.CreateDirectory(releasesService.AppPath.DirectoryPath);
                var targetFile = System.IO.Path.Combine(releasesService.AppPath.DirectoryPath, fileName);
                var tempDownload = targetFile + ".download";

                await Dispatcher.InvokeAsync(() =>
                {
                    DownloadProgress.Value = 0;
                    DownloadProgress.Visibility = Visibility.Visible;
                });

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
                        await Task.Run(() => GitHubReleaseService.ExtractZipFlatten(targetFile, releasesService.AppPath.DirectoryPath));
                        try { File.Delete(targetFile); } catch { }
                    }
                    else if (targetFile.EndsWith(".tar.gz", System.StringComparison.OrdinalIgnoreCase) || targetFile.EndsWith(".tgz", System.StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(targetFile); } catch { }
                    }
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
                await Dispatcher.InvokeAsync(() => { System.Windows.Application.Current.Shutdown(); });
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
                releasesService.releases.Clear();
            });
            try
            {
                var tmp = await Task.Run(() => GitHubReleaseService.RefreshReleases());

                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var rel in tmp)
                    {
                        releasesService.releases.Add(rel);
                    }
                });
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
                if (obj is not ReleaseInfo r)
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
            var rel = ReleasesListBox.SelectedItem as ReleaseInfo;
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
                InitialDirectory = releasesService.InstallerPath.DirectoryPath
            };

            if (folderDialog.ShowDialog() == true)
            {
                releasesService.AppPath.FilePathWrite(folderDialog.FolderName);
                InstallPathText.Text = releasesService.AppPath.DirectoryPath;
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
                    selectedRelease = ReleasesListBox.SelectedItem as ReleaseInfo;
                    if (selectedRelease == null)
                    {
                        StatusText.Text = GetString("SelectReleaseFirst");
                        return;
                    }
                    if (string.IsNullOrEmpty(releasesService.AppPath.FullPath))
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
                        StatusText.Text = $"{GetString("Installed")}: {selectedRelease.TagName}";
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
            if (!releasesService.AppPath.DirectoryExists)
            {
                StatusText.Text = GetString("ChooseInstallPathFirst");
                UpdatePanel();
                return false;
            }

            releasesService.AppPath.FilePathWrite(Path.Combine(releasesService.AppPath.DirectoryPath, ProgramExeName));

            if (!releasesService.AppPath.FileExists)
            {
                UpdatePanel();
                return false;
            }

            try
            {
                UpdatePanel();
                return true;
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
                        var prereleaseInstalled = ReleaseService.IsPrerelease(releasesService.AppVersion);

                        var latestRelease = releasesService.GetRelease();
                        var latestPrerelease = includePrerelease ? releasesService.GetPrerelease() : null;
                        ReleaseInfo? availableUpdate = (latestRelease == null && prereleaseInstalled) ? latestPrerelease : latestRelease;
                        bool updateAvailable = availableUpdate != null;

                        InstalledVersionText.Text =
                            releasesService.AppPath.FileExists
                                ? ReleaseService.FormatVersionAsTag(releasesService.AppVersion) ?? "—"
                                : GetString("InstalledNot");

                        AvailableVersionText.Text =
                            latestRelease != null
                                ? latestRelease.TagName
                                : "—";

                        if (includePrerelease && latestPrerelease != null)
                        {
                            LatestPrereleasePanel.Visibility = Visibility.Visible;
                            LatestPrereleaseText.Text = latestPrerelease.TagName;
                        }
                        else
                        {
                            LatestPrereleasePanel.Visibility = Visibility.Collapsed;
                        }

                        if (releasesService.AppPath.FileExists)
                        {
                            RunButton.Visibility = Visibility.Visible;
                            DeleteButton.Visibility = Visibility.Visible;
                            NoSelInstallButton.Visibility = Visibility.Collapsed;

                            UpdateButton.Visibility =
                                updateAvailable ? Visibility.Visible : Visibility.Collapsed;

                            NoSelectionInfoText.Text = updateAvailable
                                ? $"{GetString("Installed")}: {ReleaseService.FormatVersionAsTag(releasesService.AppVersion)} — {availableUpdate.TagName} {GetString("Available")}"
                                : $"{GetString("Installed")}: {ReleaseService.FormatVersionAsTag(releasesService.AppVersion)}";

                            RollbackToReleaseButton.Visibility =
                                prereleaseInstalled ? Visibility.Visible : Visibility.Collapsed;

                            SwitchToPrereleaseButton.Visibility =
                                (!prereleaseInstalled && latestPrerelease != null)
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                        }
                        else
                        {
                            RunButton.Visibility = Visibility.Collapsed;
                            DeleteButton.Visibility = Visibility.Collapsed;
                            UpdateButton.Visibility = Visibility.Collapsed;
                            RollbackToReleaseButton.Visibility = Visibility.Collapsed;
                            SwitchToPrereleaseButton.Visibility = Visibility.Collapsed;

                            NoSelInstallButton.Visibility =
                                (latestRelease != null || latestPrerelease != null)
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;

                            NoSelectionInfoText.Text = GetString("ReadyToInstall");
                        }
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"{GetString("Error")}: {ex.Message},  {ex.StackTrace}";
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
            foreach (var r in releasesService.releases)
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
                ProcessStartInfo startInfo = new ProcessStartInfo(releasesService.AppPath.FullPath);
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
                if (releasesService.AppPath.FileExists) File.Delete(releasesService.AppPath.FullPath);
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
                candidate = IncludePrereleaseCheck.IsChecked.GetValueOrDefault(false) && ReleaseService.IsPrerelease(releasesService.AppVersion) ? releasesService.GetAnyUpdate() : releasesService.GetRelease();
                if (ReleaseNotFound(candidate)) return;
            });
            await Task.Run(async () =>
            {
                await Install(candidate);
            });
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            if (ReleaseService.IsPrerelease(releasesService.AppVersion))
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
            var latest = releasesService.GetLatestRelease();
            if (ReleaseNotFound(latest)) return;
            Install(latest);
        }

        private void SwitchToPrereleaseButton_Click(object sender, RoutedEventArgs e)
        {
            var prerelease = releasesService.GetPrerelease();
            if (ReleaseNotFound(prerelease)) return;
            Install(prerelease);
        }

        private bool ReleaseNotFound(ReleaseInfo releaseInfo)
        {
            if (releaseInfo == null)
            {
                StatusText.Text = GetString("ReleaseNotFound");
                return true;
            }
            return false;
        }

    }
}