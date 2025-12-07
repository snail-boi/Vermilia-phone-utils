using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace phone_utils
{
    // Manages the system tray icon and menu. Kept intentionally minimal for future extension.
    internal class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly MainWindow _main;

        public TrayIconManager(MainWindow main)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Phone Utils";

            // Try to load an application icon if available, otherwise use a default system icon
            try
            {
                // Prefer a bundled logo.ico next to the exe
                var exe = Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(exe))
                {
                    var exeDir = Path.GetDirectoryName(exe);
                    var logoPath = exeDir != null ? Path.Combine(exeDir, "logo.ico") : null;
                    if (!string.IsNullOrEmpty(logoPath) && File.Exists(logoPath))
                    {
                        _notifyIcon.Icon = new Icon(logoPath);
                    }
                    else
                    {
                        // fallback to the exe associated icon
                        _notifyIcon.Icon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
                    }
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch { /* ignore icon load errors */ }

            // Build context menu
            var menu = new ContextMenuStrip();

            var startItem = new ToolStripMenuItem("Start scrcpy");
            startItem.Click += StartItem_Click;
            menu.Items.Add(startItem);

            menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += ExitItem_Click;
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = menu;

            // Double-click should also start scrcpy
            _notifyIcon.DoubleClick += (s, e) => StartScrcpy();
        }

        private void StartItem_Click(object sender, EventArgs e) => StartScrcpy();

        private void ExitItem_Click(object sender, EventArgs e)
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }

            // Ensure we call WPF Application shutdown
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        }

        private void StartScrcpy()
        {
            try
            {
                // Delegate to main window helper which performs checks and starts scrcpy for the current device
                _main?.StartScrcpyFromTray();
            }
            catch (Exception ex)
            {
                Debugger.show("Tray StartScrcpy exception: " + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch { }
        }
    }
}
