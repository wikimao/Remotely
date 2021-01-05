﻿using Remotely.Shared.Utilities;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Remotely.Agent.Services
{
    public class Updater
    {
        public Updater(ConfigService configService)
        {
            ConfigService = configService;
        }

        private SemaphoreSlim CheckForUpdatesLock { get; } = new SemaphoreSlim(1, 1);
        private ConfigService ConfigService { get; }
        private SemaphoreSlim InstallLatestVersionLock { get; } = new SemaphoreSlim(1, 1);
        private bool PreviousUpdateFailed { get; set; }
        private System.Timers.Timer UpdateTimer { get; } = new System.Timers.Timer(TimeSpan.FromHours(6).TotalMilliseconds);


        public async Task BeginChecking()
        {
            if (EnvironmentHelper.IsDebug)
            {
                return;
            }

            await CheckForUpdates();
            UpdateTimer.Elapsed += UpdateTimer_Elapsed;
            UpdateTimer.Start();
        }

        public async Task CheckForUpdates()
        {
            try
            {
                await CheckForUpdatesLock.WaitAsync();

                if (EnvironmentHelper.IsDebug)
                {
                    return;
                }

                if (PreviousUpdateFailed)
                {
                    Logger.Write("Skipping update check due to previous failure.  Restart the service to try again, or manually install the update.");
                    return;
                }


                var connectionInfo = ConfigService.GetConnectionInfo();
                var serverUrl = ConfigService.GetConnectionInfo().Host;

                string fileUrl;

                if (EnvironmentHelper.IsWindows)
                {
                    var platform = Environment.Is64BitOperatingSystem ? "x64" : "x86";
                    fileUrl = serverUrl + $"/Downloads/Remotely-Win10-{platform}.zip";
                }
                else if (EnvironmentHelper.IsLinux)
                {
                    fileUrl = serverUrl + $"/Downloads/Remotely-Linux.zip";
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }

                var lastEtag = string.Empty;

                if (File.Exists("etag.txt"))
                {
                    lastEtag = await File.ReadAllTextAsync("etag.txt");
                }

                try
                {
                    var wr = WebRequest.CreateHttp(fileUrl);
                    wr.Method = "Head";
                    wr.Headers.Add("If-None-Match", lastEtag);
                    using var response = (HttpWebResponse)await wr.GetResponseAsync();
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        Logger.Write("Service Updater: Version is current.");
                        return;
                    }
                }
                catch (WebException ex) when ((ex.Response as HttpWebResponse).StatusCode == HttpStatusCode.NotModified)
                {
                    Logger.Write("Service Updater: Version is current.");
                    return;
                }

                Logger.Write("Service Updater: Update found.");

                await InstallLatestVersion();

            }
            catch (Exception ex)
            {
                Logger.Write(ex);
            }
            finally
            {
                CheckForUpdatesLock.Release();
            }
        }

        public async Task InstallLatestVersion()
        {
            try
            {
                await InstallLatestVersionLock.WaitAsync();

                var connectionInfo = ConfigService.GetConnectionInfo();
                var serverUrl = connectionInfo.Host;

                Logger.Write("Service Updater: Downloading install package.");

                using var wc = new WebClientEx((int)UpdateTimer.Interval);
                var downloadId = Guid.NewGuid().ToString();
                var zipPath = Path.Combine(Path.GetTempPath(), "RemotelyUpdate.zip");

                if (EnvironmentHelper.IsWindows)
                {
                    var installerPath = Path.Combine(Path.GetTempPath(), "Remotely_Installer.exe");
                    var platform = Environment.Is64BitOperatingSystem ? "x64" : "x86";

                    await wc.DownloadFileTaskAsync(
                         serverUrl + $"/Downloads/Remotely_Installer.exe",
                         installerPath);

                    await wc.DownloadFileTaskAsync(
                       serverUrl + $"/api/AgentUpdate/DownloadPackage/win-{platform}/{downloadId}",
                       zipPath);

                    (await WebRequest.CreateHttp(serverUrl + $"/api/AgentUpdate/ClearDownload/{downloadId}").GetResponseAsync()).Dispose();


                    foreach (var proc in Process.GetProcessesByName("Remotely_Installer"))
                    {
                        proc.Kill();
                    }

                    Logger.Write("Launching installer to perform update.");

                    Process.Start(installerPath, $"-install -quiet -path {zipPath} -serverurl {serverUrl} -organizationid {connectionInfo.OrganizationID}");
                }
                else if (EnvironmentHelper.IsLinux)
                {
                    var installerPath = Path.Combine(Path.GetTempPath(), "RemotelyUpdate.sh");

                    string platform;

                    if (RuntimeInformation.OSDescription.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase))
                    {
                        platform = "Ubuntu-x64";
                    }
                    else if (RuntimeInformation.OSDescription.Contains("Manjaro", StringComparison.OrdinalIgnoreCase))
                    {
                        platform = "Manjaro-x64";
                    }
                    else
                    {
                        throw new PlatformNotSupportedException();
                    }

                    await wc.DownloadFileTaskAsync(
                           serverUrl + $"/API/ClientDownloads/{connectionInfo.OrganizationID}/{platform}",
                           installerPath);

                    await wc.DownloadFileTaskAsync(
                       serverUrl + $"/API/AgentUpdate/DownloadPackage/linux/{downloadId}",
                       zipPath);

                    (await WebRequest.CreateHttp(serverUrl + $"/api/AgentUpdate/ClearDownload/{downloadId}").GetResponseAsync()).Dispose();

                    Logger.Write("Launching installer to perform update.");

                    Process.Start("sudo", $"chmod +x {installerPath}").WaitForExit();

                    Process.Start("sudo", $"{installerPath} --path {zipPath} & disown");
                }
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.Timeout)
            {
                Logger.Write("Timed out while waiting to download update.", Shared.Enums.EventType.Warning);
                PreviousUpdateFailed = true;
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
                PreviousUpdateFailed = true;
            }
            finally
            {
                InstallLatestVersionLock.Release();
            }
        }

        private async void UpdateTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            await CheckForUpdates();
        }
        private class WebClientEx : WebClient
        {
            private readonly int _requestTimeout;

            public WebClientEx(int requestTimeout)
            {
                _requestTimeout = requestTimeout;
            }
            protected override WebRequest GetWebRequest(Uri uri)
            {
                WebRequest webRequest = base.GetWebRequest(uri);
                webRequest.Timeout = _requestTimeout;
                return webRequest;
            }
        }
    }
}
