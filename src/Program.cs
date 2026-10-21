using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

// Built with the in-box csc, which does not add this; without it the runtime
// falls back to .NET 4.0 compatibility behaviour (old TLS defaults, no DPI support).
[assembly: TargetFramework(".NETFramework,Version=v4.8")]

namespace HassWidgets
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // One instance per copy of the exe, since each copy owns its config folder.
            string name = "HassWidgets_" + AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant().GetHashCode().ToString("X8");
            bool first;
            using (new Mutex(true, name, out first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App());
            }
        }
    }

    // Tray icon plus the set of widget windows; there is no main window.
    class App : ApplicationContext
    {
        public static readonly Icon AppIcon = LoadIcon();

        readonly AppConfig config;
        readonly List<WidgetForm> widgets = new List<WidgetForm>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
        Form activeDialog;

        public IEnumerable<WidgetForm> Widgets { get { return widgets; } }

        public App()
        {
            string problem;
            config = ConfigStore.Load(out problem);
            Hass.Configure(config);

            saveTimer.Interval = 800;
            saveTimer.Tick += (s, e) => SaveNow();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Add widget...", null, (s, e) => AddWidget());
            menu.Items.Add("Show all widgets", null, (s, e) => ShowAll());
            menu.Items.Add("Refresh all", null, (s, e) => ReloadAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Connection settings...", null, (s, e) => ShowSettings());
            menu.Items.Add("Open config folder", null, (s, e) => OpenConfigFolder());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => Exit());

            tray.Icon = AppIcon;
            tray.Text = "HASS Widgets";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => ShowAll();
            tray.Visible = true;

            foreach (WidgetConfig w in config.Widgets) CreateWidget(w);

            // Dialogs need the message loop, so the first-run prompts wait for it.
            EventHandler onIdle = null;
            onIdle = (s, e) =>
            {
                Application.Idle -= onIdle;
                if (problem != null)
                    MessageBox.Show(problem, "HASS Widgets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (!Hass.IsConfigured) ShowSettings();
                if (Hass.IsConfigured && config.Widgets.Count == 0) AddWidget();
            };
            Application.Idle += onIdle;
        }

        static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { return SystemIcons.Application; }
        }

        WidgetForm CreateWidget(WidgetConfig cfg)
        {
            var form = new WidgetForm(this, cfg);
            widgets.Add(form);
            form.Show();
            return form;
        }

        // Only one dialog at a time; a second request brings the open one forward.
        DialogResult ShowModal(Form dialog)
        {
            if (activeDialog != null)
            {
                activeDialog.Activate();
                return DialogResult.Cancel;
            }
            activeDialog = dialog;
            try { return dialog.ShowDialog(); }
            finally { activeDialog = null; }
        }

        public void AddWidget()
        {
            var cfg = new WidgetConfig();
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int offset = 28 * (widgets.Count % 8);
            cfg.X = area.X + 60 + offset;
            cfg.Y = area.Y + 60 + offset;

            WidgetForm form = null;
            using (var dialog = new WidgetDialog(cfg, true))
            {
                dialog.Applied += () =>
                {
                    if (form == null)
                    {
                        config.Widgets.Add(cfg);
                        form = CreateWidget(cfg);
                    }
                    else form.ApplyConfig();
                    SaveSoon();
                };
                ShowModal(dialog);
            }
        }

        public void EditWidget(WidgetForm form)
        {
            using (var dialog = new WidgetDialog(form.Cfg, false))
            {
                dialog.Applied += () => { form.ApplyConfig(); SaveSoon(); };
                ShowModal(dialog);
            }
        }

        public void DuplicateWidget(WidgetForm form)
        {
            WidgetConfig cfg = form.Cfg.Clone();
            cfg.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            cfg.X += 24;
            cfg.Y += 24;
            cfg.Locked = false;
            config.Widgets.Add(cfg);
            CreateWidget(cfg);
            SaveSoon();
        }

        public void RemoveWidget(WidgetForm form)
        {
            if (MessageBox.Show("Remove this widget?", "HASS Widgets", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            config.Widgets.Remove(form.Cfg);
            widgets.Remove(form);
            form.Close();
            form.Dispose();
            SaveSoon();
        }

        public void ShowSettings()
        {
            using (var dialog = new SettingsDialog(config))
            {
                if (ShowModal(dialog) != DialogResult.OK) return;
            }
            Hass.Configure(config);
            SaveNow();
            ReloadAll();
        }

        void ShowAll()
        {
            foreach (WidgetForm w in widgets)
            {
                w.Show();
                w.BringToFront();
            }
        }

        void ReloadAll()
        {
            foreach (WidgetForm w in widgets) w.Reload();
        }

        void OpenConfigFolder()
        {
            try
            {
                SaveNow();
                Process.Start(ConfigStore.Dir);
            }
            catch (Exception) { }
        }

        // Coalesces bursts of changes (dragging, wheel scaling) into one write.
        public void SaveSoon()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        void SaveNow()
        {
            saveTimer.Stop();
            try
            {
                ConfigStore.Save(config);
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "HASS Widgets", "Could not save settings: " + ex.Message, ToolTipIcon.Warning);
            }
        }

        public void Exit()
        {
            SaveNow();
            tray.Visible = false;
            foreach (WidgetForm w in widgets.ToArray()) w.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tray.Dispose();
                saveTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
