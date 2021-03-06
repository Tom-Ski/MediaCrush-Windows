﻿using MediaCrush;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;

namespace MediaCrush
{
    public static class Program
    {
        static UploadWindow UploadWindow;
        static NotifyIcon NotifyIcon;
        static MenuItem UpdateMenuItem;
        static string UpdateUrl;
        public static SettingsManager SettingsManager;
        public static readonly Version Version = new Version(1, 0, 0);

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            SettingsManager.Initialize();
            SettingsManager = new SettingsManager();
            LoadSettings();
            SettingsManager.ForcePropertyUpdate();

            bool firstRun = false, startup = false;
            foreach (var arg in args)
            {
                if (arg == "--startup")
                {
                    if (!SettingsManager.RunOnStartup)
                        return;
                    startup = true;
                }
                if (arg == "--firstrun")
                    firstRun = true;
            }

            if (SettingsManager.CheckForUpdates)
                CheckForUpdates();
            // Note to people reading this source code: analytics is opt-in and no data is sent by calling this method unless users have done so
            Analytics.TrackFeatureUse("Application startup");
            const int minutesBetweenIdleUpdate = 5;
            var timer = new System.Threading.Timer(o => Analytics.TrackFeatureUse("Idling"), null, minutesBetweenIdleUpdate * 60 * 1000, minutesBetweenIdleUpdate * 60 * 1000);

            _hookID = SetHook(_proc);

            NotifyIcon = new NotifyIcon();
            NotifyIcon.Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
            NotifyIcon.Visible = true;
            NotifyIcon.Text = "Click here to upload files to MediaCrush";

            var menu = new ContextMenu();
            UpdateMenuItem = new MenuItem("Update to ");
            UpdateMenuItem.Visible = false;
            var settings = new MenuItem("Settings");
            var source = new MenuItem("Source Code");
            var exit = new MenuItem("Exit");
            menu.MenuItems.Add(UpdateMenuItem);
            //menu.MenuItems.Add(settings);
            menu.MenuItems.Add(source);
            menu.MenuItems.Add(exit);
            UpdateMenuItem.Click += (s, e) => UpdateToVersion((Version)UpdateMenuItem.Tag, UpdateUrl);
            source.Click += (s, e) => Process.Start("https://github.com/MediaCrush/MediaCrush-Windows");
            exit.Click += (s, e) => { Analytics.TrackFeatureUse("Manual shutdown"); Application.Exit(); };
            settings.Click += settings_Click;
            NotifyIcon.ContextMenu = menu;

            NotifyIcon.Click += icon_Click;

            UploadWindow = new UploadWindow();
            UploadWindow.Show();
            if (startup)
                UploadWindow.Visibility = System.Windows.Visibility.Hidden;

            Application.ApplicationExit += (s, e) => NotifyIcon.Dispose();
            Application.Run();
            UnhookWindowsHookEx(_hookID);
        }

        private static void CheckForUpdates()
        {
            Task.Factory.StartNew(() =>
            {
                var json = JToken.Parse(new WebClient().DownloadString("https://mediacru.sh/static/windows.json"));
                var latest = new Version(json["version"].Value<string>());
                UpdateUrl = json["url"].Value<string>();
                if (Version < latest)
                {
                    UpdateMenuItem.Text += latest;
                    UpdateMenuItem.Visible = true;
                    UpdateMenuItem.Tag = latest;
                    NotifyIcon.BalloonTipClicked += (s, e) => UpdateToVersion(latest, UpdateUrl);
                    NotifyIcon.ShowBalloonTip(5000, "Update Available", "An update is available. Click here for details.", ToolTipIcon.Info);
                }
            });
        }

        private static void UpdateToVersion(Version version, string update)
        {
            var details = new UpdateDetails(version);
            if (details.ShowDialog().GetValueOrDefault(false))
            {
                // Download and install update
                Task.Factory.StartNew(() =>
                {
                    var client = new WebClient();
                    var temp = Path.GetTempFileName() + ".exe";
                    client.DownloadFile(update, temp);
                    Process.Start(temp);
                    Application.Exit();
                });
            }
        }

        public static void LoadSettings()
        {
            if (!File.Exists(SettingsManager.SettingsFile))
            {
                SettingsManager.SetToDefaults();
                SaveSettings();
            }
            else
            {
                var serializer = new JsonSerializer();
                serializer.Formatting = Formatting.Indented;
                serializer.MissingMemberHandling = MissingMemberHandling.Ignore;
                try
                {
                    using (var reader = new StreamReader(SettingsManager.SettingsFile))
                        serializer.Populate(reader, SettingsManager);
                }
                catch
                {
                    System.Windows.MessageBox.Show("Your settings are corrupted. They have been reset to the defaults.",
                        "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    SettingsManager.SetToDefaults();
                    SaveSettings();
                }
            }
        }

        public static void SaveSettings()
        {
            var serializer = new JsonSerializer();
            serializer.Formatting = Formatting.Indented;
            using (var writer = new StreamWriter(SettingsManager.SettingsFile))
                serializer.Serialize(writer, SettingsManager);
        }

        static void icon_Click(object sender, EventArgs e)
        {
            if (UploadWindow.Visibility == System.Windows.Visibility.Visible)
                UploadWindow.Visibility = System.Windows.Visibility.Hidden;
            else
                UploadWindow.Visibility = System.Windows.Visibility.Visible;
        }

        static void settings_Click(object sender, EventArgs e)
        {
            throw new NotImplementedException();
        }

        private static void CaptureScreenshot()
        {
            Analytics.TrackFeatureUse("Screenshot");
            UploadWindow.Dispatcher.Invoke(new Action(() =>
            {
                var screen = CaptureVirtualScreen(); // Capture the screen as it appears the moment they press the key combo
                var tool = new ScreenCapture(screen);
                if (tool.ShowDialog().GetValueOrDefault(true))
                {
                    var bitmap = new Bitmap((int)tool.Selection.Width, (int)tool.Selection.Height);
                    int left = (int)tool.Selection.Left;
                    int top = (int)tool.Selection.Top;
                    using (var graphics = Graphics.FromImage(bitmap))
                        graphics.DrawImage(screen, new Point(-left, -top));
                    var file = Path.GetTempFileName() + ".png";
                    bitmap.Save(file, ImageFormat.Png);
                    UploadWindow.Visibility = System.Windows.Visibility.Visible;
                    UploadWindow.UploadFile(file);
                }
            }));
        }

        private static Bitmap CaptureVirtualScreen()
        {
            int left = SystemInformation.VirtualScreen.Left;
            int top = SystemInformation.VirtualScreen.Top;
            int width = SystemInformation.VirtualScreen.Width;
            int height = SystemInformation.VirtualScreen.Height;

            var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(left, top, 0, 0, bitmap.Size);
            return bitmap;
        }

        private static bool ControlPressed = false;
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) 
        {
            if (!SettingsManager.EnableScreenCapture)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            if (nCode >= 0)
            {
                Keys number = (Keys)Marshal.ReadInt32(lParam);
                if (wParam.ToInt32() == WM_KEYDOWN)
                {
                    if (number == Keys.LControlKey || number == Keys.RControlKey || number == Keys.Control || number == Keys.ControlKey)
                        ControlPressed = true;
                }
                else if (wParam.ToInt32() == WM_KEYUP)
                {
                    if (number == Keys.LControlKey || number == Keys.RControlKey || number == Keys.Control || number == Keys.ControlKey)
                        ControlPressed = false;
                    else if (number == Keys.PrintScreen && ControlPressed)
                    {
                        var thread = new Thread(new ThreadStart(CaptureScreenshot));
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        private delegate IntPtr LowLevelKeyboardProc( int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);  
    }
}