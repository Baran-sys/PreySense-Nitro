using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using PreySense.Helpers;

namespace PreySense.Frontend.Services
{
    public static class UpdateCheckService
    {
        public static string CurrentVersion => typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        public static string DisplayVersion => "v" + CurrentVersion;
        private const string LatestReleaseApi = "https://api.github.com/repos/Baran-sys/PreySense/releases/latest";
        private const string ReleasesUrl = "https://github.com/Baran-sys/PreySense/releases";

        public static async Task CheckForUpdatesAsync(bool isManualCheck = false)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PreySense");
                client.Timeout = TimeSpan.FromSeconds(8);

                string json = await client.GetStringAsync(LatestReleaseApi);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagProperty))
                {
                    if (isManualCheck) await ShowUpToDateNoticeAsync();
                    return;
                }

                string? latestTag = tagProperty.GetString();
                if (string.IsNullOrWhiteSpace(latestTag))
                {
                    if (isManualCheck) await ShowUpToDateNoticeAsync();
                    return;
                }

                if (!Version.TryParse(latestTag.TrimStart('v', 'V'), out Version? latest) ||
                    !Version.TryParse(CurrentVersion, out Version? current) ||
                    latest <= current)
                {
                    if (isManualCheck) await ShowUpToDateNoticeAsync();
                    return;
                }

                string releaseUrl = ReleasesUrl;
                if (doc.RootElement.TryGetProperty("html_url", out var htmlUrl) &&
                    htmlUrl.GetString() is { Length: > 0 } pageUrl)
                {
                    releaseUrl = pageUrl;
                }

                bool openDownload = await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mw = Application.Current.MainWindow;
                    if (!isManualCheck && (mw == null || !mw.IsVisible))
                        return false;

                    var dialog = new Views.CustomModalDialog
                    {
                        Owner = mw,
                        Title = "PreySense — Update Available",
                        HeaderSubtitle = " — Update Available",
                        Message = LocalizationService.Format("Version {0} of PreySense is available.", latestTag),
                        ConfirmText = "Download",
                        CancelText = "Later",
                        ShowCancelButton = true
                    };

                    return dialog.ShowDialog() == true;
                });

                if (!openDownload)
                    return;

                Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                string msg = (ex is HttpRequestException || ex is System.Net.Sockets.SocketException)
                    ? $"Update check skipped (network unreachable: {ex.Message})"
                    : $"Update check failed: {ex.Message}";
                AppLogger.Log(msg);

                if (isManualCheck)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dialog = new Views.CustomModalDialog
                        {
                            Owner = Application.Current.MainWindow,
                            Title = "PreySense",
                            HeaderSubtitle = LocalizationService.Translate(" — Update Check"),
                            Message = LocalizationService.Translate("PreySense could not connect to GitHub. Please check your internet connection."),
                            ConfirmText = LocalizationService.Translate("OK"),
                            ShowCancelButton = false
                        };
                        dialog.ShowDialog();
                    });
                }
            }
        }

        private static async Task ShowUpToDateNoticeAsync()
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Views.CustomModalDialog
                {
                    Owner = Application.Current.MainWindow,
                    Title = "PreySense",
                    HeaderSubtitle = LocalizationService.Translate(" — Update Check"),
                    Message = LocalizationService.Format("PreySense is up to date (v{0}).", CurrentVersion),
                    ConfirmText = LocalizationService.Translate("OK"),
                    ShowCancelButton = false
                };
                dialog.ShowDialog();
            });
        }
    }
}
