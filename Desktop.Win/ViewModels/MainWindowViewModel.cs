﻿using Microsoft.Extensions.DependencyInjection;
using Remotely.Desktop.Core;
using Remotely.Desktop.Core.Interfaces;
using Remotely.Desktop.Core.Services;
using Remotely.Desktop.Core.ViewModels;
using Remotely.Desktop.Win.Controls;
using Remotely.Desktop.Win.Services;
using Remotely.Shared.Models;
using Remotely.Shared.Utilities;
using Remotely.Shared.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Remotely.Desktop.Win.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private string _host;
        private string _sessionID;

        public MainWindowViewModel()
        {
            Current = this;

            if (Services is null)
            {
                return;
            }

            Application.Current.Exit += Application_Exit;

            CursorIconWatcher = Services?.GetRequiredService<ICursorIconWatcher>();
            CursorIconWatcher.OnChange += CursorIconWatcher_OnChange;
            Services.GetRequiredService<IClipboardService>().BeginWatching();
            Services.GetRequiredService<IKeyboardMouseInput>().Init();
            Conductor = Services.GetRequiredService<Conductor>();
            CasterSocket = Services.GetRequiredService<ICasterSocket>();
            Conductor.SessionIDChanged += SessionIDChanged;
            Conductor.ViewerRemoved += ViewerRemoved;
            Conductor.ViewerAdded += ViewerAdded;
            Conductor.ScreenCastRequested += ScreenCastRequested;
        }

        public static MainWindowViewModel Current { get; private set; }
        public static IServiceProvider Services => ServiceContainer.Instance;

        public ICommand ChangeServerCommand
        {
            get
            {
                return new Executor(async (param) =>
                {
                    PromptForHostName();
                    await Init();
                });
            }
        }

        public ICommand ElevateToAdminCommand
        {
            get
            {
                return new Executor((param) =>
                {
                    try
                    {
                        //var filePath = Process.GetCurrentProcess().MainModule.FileName;
                        var commandLine = Win32Interop.GetCommandLine().Replace(" -elevate", "");
                        var sections = commandLine.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        var filePath = sections.First();
                        var arguments = string.Join('"', sections.Skip(1));
                        var psi = new ProcessStartInfo(filePath, arguments)
                        {
                            Verb = "RunAs",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                        Environment.Exit(0);
                    }
                    // Exception can be thrown if UAC is dialog is cancelled.
                    catch { }
                }, (param) =>
                {
                    return !IsAdministrator;
                });
            }
        }

        public ICommand ElevateToServiceCommand
        {
            get
            {
                return new Executor((param) =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("cmd.exe")
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        };
                        //var filePath = Process.GetCurrentProcess().MainModule.FileName;
                        var commandLine = Win32Interop.GetCommandLine().Replace(" -elevate", "");
                        var sections = commandLine.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        var filePath = sections.First();
                        var arguments = string.Join('"', sections.Skip(1));
                        Logger.Write($"Creating temporary service with file path {filePath} and arguments {arguments}.");
                        psi.Arguments = $"/c sc create Remotely_Temp binPath=\"{filePath} {arguments} -elevate\"";
                        Process.Start(psi).WaitForExit();
                        psi.Arguments = "/c sc start Remotely_Temp";
                        Process.Start(psi).WaitForExit();
                        psi.Arguments = "/c sc delete Remotely_Temp";
                        Process.Start(psi).WaitForExit();
                        App.Current.Shutdown();
                    }
                    catch { }
                }, (param) =>
                {
                    return IsAdministrator && !WindowsIdentity.GetCurrent().IsSystem;
                });
            }
        }

        public string Host
        {
            get => _host;
            set
            {
                _host = value;
                FirePropertyChanged(nameof(Host));
            }
        }

        public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public ICommand RemoveViewersCommand
        {
            get
            {
                return new Executor(async (param) =>
                {
                    foreach (Viewer viewer in (param as IList<object>).ToArray())
                    {
                        ViewerRemoved(this, viewer.ViewerConnectionID);
                        await CasterSocket.DisconnectViewer(viewer, true);
                    }
                },
                (param) =>
                {
                    return (param as IList<object>)?.Count > 0;
                });
            }

        }

        public string SessionID
        {
            get => _sessionID;
            set
            {
                _sessionID = value;
                FirePropertyChanged(nameof(SessionID));
            }
        }

        public ObservableCollection<Viewer> Viewers { get; } = new ObservableCollection<Viewer>();

        private ICasterSocket CasterSocket { get; set; }

        private Conductor Conductor { get; set; }

        private ICursorIconWatcher CursorIconWatcher { get; set; }

        public void CopyLink()
        {
            Clipboard.SetText($"{Host}/RemoteControl?sessionID={SessionID?.Replace(" ", "")}");
        }

        public async Task GetSessionID()
        {
            await CasterSocket.SendDeviceInfo(Conductor.ServiceID, Environment.MachineName, Conductor.DeviceID);
            await CasterSocket.GetSessionID();
        }

        public async Task Init()
        {
            SessionID = "Retrieving...";

            Host = Config.GetConfig().Host;

            while (string.IsNullOrWhiteSpace(Host))
            {
                Host = "https://";
                PromptForHostName();
            }

            Conductor.ProcessArgs(new string[] { "-mode", "Normal", "-host", Host });
            try
            {
                await CasterSocket.Connect(Conductor.Host);

                CasterSocket.Connection.Closed += async (ex) =>
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Viewers.Clear();
                        SessionID = "Disconnected";
                    });
                };

                CasterSocket.Connection.Reconnecting += async (ex) =>
                {
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Viewers.Clear();
                        SessionID = "Reconnecting";
                    });
                };

                CasterSocket.Connection.Reconnected += async (arg) =>
                {
                    await GetSessionID();
                };

                await GetSessionID();
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
                SessionID = "Failed";
                MessageBox.Show(Application.Current.MainWindow, "Failed to connect to server.", "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        public void PromptForHostName()
        {
            var prompt = new HostNamePrompt();
            if (!string.IsNullOrWhiteSpace(Host))
            {
                prompt.ViewModel.Host = Host;
            }
            prompt.Owner = App.Current?.MainWindow;
            prompt.ShowDialog();
            var result = prompt.ViewModel.Host;
            if (!result.StartsWith("https://") && !result.StartsWith("http://"))
            {
                result = $"https://{result}";
            }
            if (result != Host)
            {
                Host = result.TrimEnd('/');
                var config = Config.GetConfig();
                config.Host = Host;
                config.Save();
            }
        }

        public void ShutdownApp()
        {
            Services.GetRequiredService<IShutdownService>().Shutdown();
        }
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Viewers.Clear();
            });
        }

        private async void CursorIconWatcher_OnChange(object sender, CursorInfo cursor)
        {
            if (Conductor?.Viewers?.Count > 0)
            {
                foreach (var viewer in Conductor.Viewers.Values)
                {
                    await viewer.SendCursorChange(cursor);
                }
            }
        }

        private void ScreenCastRequested(object sender, ScreenCastRequest screenCastRequest)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                App.Current.MainWindow.Activate();
                var result = MessageBox.Show(Application.Current.MainWindow, $"You've received a connection request from {screenCastRequest.RequesterName}.  Accept?", "Connection Request", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Task.Run(() =>
                    {
                        Services.GetRequiredService<IScreenCaster>().BeginScreenCasting(screenCastRequest);
                    });
                }
                else
                {
                    // Run on another thread so it doesn't tie up the UI thread.
                    Task.Run(async () =>
                    {
                        await CasterSocket.SendConnectionRequestDenied(screenCastRequest.ViewerID);
                    });
                }
            });
        }
        private void SessionIDChanged(object sender, string sessionID)
        {
            var formattedSessionID = "";
            for (var i = 0; i < sessionID.Length; i += 3)
            {
                formattedSessionID += sessionID.Substring(i, 3) + " ";
            }
            SessionID = formattedSessionID.Trim();
        }

        private void ViewerAdded(object sender, Viewer viewer)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Viewers.Add(viewer);
            });
        }

        private void ViewerRemoved(object sender, string viewerID)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                var viewer = Viewers.FirstOrDefault(x => x.ViewerConnectionID == viewerID);
                if (viewer != null)
                {
                    Viewers.Remove(viewer);
                }
            });
        }
    }
}
