using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Windows.Forms;

namespace HassWidgets
{
    // Shared two-column "label: control" layout for the dialogs.
    abstract class DialogBase : Form
    {
        protected readonly TableLayoutPanel Table = new TableLayoutPanel();
        protected readonly FlowLayoutPanel Buttons = new FlowLayoutPanel();

        protected DialogBase(string title)
        {
            Text = title;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Icon = App.AppIcon;

            Table.ColumnCount = 2;
            Table.AutoSize = true;
            Table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Table.Padding = new Padding(U(10));
            Table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, U(360)));
            Controls.Add(Table);

            Buttons.FlowDirection = FlowDirection.RightToLeft;
            Buttons.AutoSize = true;
            Buttons.Dock = DockStyle.Fill;
            Buttons.Margin = new Padding(0, U(8), 0, 0);
        }

        protected int U(int px)
        {
            return px * DeviceDpi / 96;
        }

        protected T Row<T>(string label, T control, bool stretch) where T : Control
        {
            var l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, U(3), U(8), U(3));
            control.Anchor = stretch ? (AnchorStyles.Left | AnchorStyles.Right) : AnchorStyles.Left;
            Table.Controls.Add(l);
            Table.Controls.Add(control);
            return control;
        }

        protected T Wide<T>(T control) where T : Control
        {
            Table.Controls.Add(control);
            Table.SetColumnSpan(control, 2);
            return control;
        }

        protected Button AddButton(string text)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.MinimumSize = new Size(U(84), U(26));
            Buttons.Controls.Add(b);
            return b;
        }

        protected void FinishLayout()
        {
            Wide(Buttons);
        }
    }

    class SettingsDialog : DialogBase
    {
        readonly AppConfig cfg;
        readonly TextBox url = new TextBox();
        readonly TextBox token = new TextBox();
        readonly CheckBox ignoreCert = new CheckBox();
        readonly Label status = new Label();
        readonly Button test;

        public SettingsDialog(AppConfig cfg) : base("Home Assistant connection")
        {
            this.cfg = cfg;
            url.Text = cfg.BaseUrl;
            token.Text = cfg.Token;
            token.UseSystemPasswordChar = true;
            ignoreCert.Text = "Ignore HTTPS certificate errors (self-signed)";
            ignoreCert.AutoSize = true;
            ignoreCert.Checked = cfg.IgnoreCertErrors;

            Row("Server URL", url, true);
            Row("Access token", token, true);
            Row("", ignoreCert, false);

            var hint = new Label();
            hint.Text = "Create a token in Home Assistant under your profile:\nSecurity > Long-lived access tokens.";
            hint.AutoSize = true;
            hint.ForeColor = SystemColors.GrayText;
            hint.Margin = new Padding(0, U(6), 0, U(6));
            Wide(hint);

            status.AutoSize = true;
            status.MaximumSize = new Size(U(380), 0);
            Wide(status);

            Button cancel = AddButton("Cancel");
            Button ok = AddButton("OK");
            test = AddButton("Test");
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => { Store(); DialogResult = DialogResult.OK; };
            test.Click += (s, e) => Test();
            AcceptButton = ok;
            CancelButton = cancel;
            FinishLayout();
        }

        void Store()
        {
            cfg.BaseUrl = Hass.NormalizeUrl(url.Text);
            cfg.Token = token.Text.Trim();
            cfg.IgnoreCertErrors = ignoreCert.Checked;
        }

        async void Test()
        {
            bool previous = Hass.IgnoreCertErrors;
            test.Enabled = false;
            status.ForeColor = SystemColors.ControlText;
            status.Text = "Connecting...";
            try
            {
                Hass.IgnoreCertErrors = ignoreCert.Checked;
                await Hass.Send(HttpMethod.Get, Hass.NormalizeUrl(url.Text), token.Text.Trim(), "/api/", null);
                if (IsDisposed) return;
                status.ForeColor = Color.Green;
                status.Text = "Connected.";
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                status.ForeColor = Color.Firebrick;
                status.Text = Hass.Describe(ex);
            }
            finally
            {
                Hass.IgnoreCertErrors = previous;
                if (!IsDisposed) test.Enabled = true;
            }
        }
    }

    class WidgetDialog : DialogBase
    {
        // Raised after the controls have been written back to the config.
        public event Action Applied;

        readonly WidgetConfig cfg;
        readonly ComboBox device = new ComboBox();
        DeviceItem allDevices, noDevice, current;
        readonly TextBox filter = new TextBox();
        readonly Button add = new Button();
        readonly DataGridView entities = new DataGridView();
        readonly Dictionary<string, string> labels;   // display names typed for entities
        readonly List<string> selected;     // ticked entity ids, in the order the widget shows them
        bool filling;
        Button ok;
        readonly TextBox title = new TextBox();
        readonly CheckBox showTitle = new CheckBox();
        readonly ComboBox mode = new ComboBox();
        readonly NumericUpDown poll = new NumericUpDown();
        readonly NumericUpDown hours = new NumericUpDown();
        readonly NumericUpDown decimals = new NumericUpDown();
        readonly TextBox min = new TextBox();
        readonly TextBox max = new TextBox();
        readonly TextBox unit = new TextBox();
        readonly CheckBox showUnit = new CheckBox();
        readonly Button backColor, titleColor, entityColor, textColor, accentColor;
        readonly NumericUpDown opacity = new NumericUpDown();
        readonly CheckBox topMost = new CheckBox();

        public WidgetDialog(WidgetConfig cfg, bool isNew) : base(isNew ? "Add widget" : "Configure widget")
        {
            this.cfg = cfg;
            TopMost = false;

            device.DropDownStyle = ComboBoxStyle.DropDownList;
            device.Items.Add("Loading...");
            device.SelectedIndex = 0;
            device.Enabled = false;
            device.SelectedIndexChanged += (s, e) =>
            {
                var d = device.SelectedItem as DeviceItem;
                if (d != null) ShowEntities(d);
            };
            Row("Device", device, true);

            selected = cfg.AllEntities();
            labels = new Dictionary<string, string>(cfg.Labels ?? new Dictionary<string, string>());

            // Narrows the list below; also accepts an entity id that is not listed.
            var find = new TableLayoutPanel();
            find.ColumnCount = 2;
            find.AutoSize = true;
            find.Margin = new Padding(0);
            find.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            find.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            filter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            add.Text = "Add";
            add.AutoSize = true;
            add.Enabled = false;
            add.Margin = new Padding(U(4), 0, U(3), 0);
            find.Controls.Add(filter);
            find.Controls.Add(add);
            Row("Filter, or add an entity id", find, true);

            // Tick column, entity id, and an editable name shown in the widget.
            var tick = new DataGridViewCheckBoxColumn();
            tick.HeaderText = "";
            tick.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            tick.Width = U(26);
            tick.Resizable = DataGridViewTriState.False;
            var idColumn = new DataGridViewTextBoxColumn();
            idColumn.HeaderText = "Entity";
            idColumn.ReadOnly = true;
            idColumn.FillWeight = 60;
            var nameColumn = new DataGridViewTextBoxColumn();
            nameColumn.HeaderText = "Display name";
            nameColumn.FillWeight = 40;
            entities.Columns.AddRange(tick, idColumn, nameColumn);
            foreach (DataGridViewColumn c in entities.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            entities.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            entities.RowHeadersVisible = false;
            entities.AllowUserToAddRows = false;
            entities.AllowUserToDeleteRows = false;
            entities.AllowUserToResizeRows = false;
            entities.MultiSelect = false;
            entities.EditMode = DataGridViewEditMode.EditOnEnter;
            entities.BackgroundColor = SystemColors.Window;
            entities.BorderStyle = BorderStyle.FixedSingle;
            entities.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            entities.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            entities.ScrollBars = ScrollBars.Vertical;
            entities.Height = U(150);
            Row("Entities (tick one or more)", entities, true);

            filter.TextChanged += (s, e) => RefreshEntities();
            // Enter in the filter adds the typed id instead of closing the dialog.
            filter.Enter += (s, e) => AcceptButton = null;
            filter.Leave += (s, e) => AcceptButton = ok;
            filter.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                AddTyped();
            };
            add.Click += (s, e) => AddTyped();
            // A tick takes effect on the click, not when the cell loses focus.
            entities.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (entities.CurrentCell is DataGridViewCheckBoxCell)
                    entities.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            entities.CellValueChanged += (s, e) =>
            {
                if (filling || e.RowIndex < 0) return;
                DataGridViewRow row = entities.Rows[e.RowIndex];
                string id = (string)row.Cells[1].Value;
                if (e.ColumnIndex == 0)
                {
                    if (!true.Equals(row.Cells[0].Value)) selected.Remove(id);
                    else if (!selected.Contains(id)) selected.Add(id);
                    SuggestTitle();
                }
                else if (e.ColumnIndex == 2)
                {
                    string name = (row.Cells[2].Value as string ?? "").Trim();
                    if (name.Length == 0) labels.Remove(id);
                    else labels[id] = name;
                }
            };
            RefreshEntities();

            title.Text = cfg.Title;
            Row("Title (blank = automatic)", title, true);
            Check(showTitle, "Show title", cfg.ShowTitle);

            mode.DropDownStyle = ComboBoxStyle.DropDownList;
            mode.Items.AddRange(DisplayModes.Labels);
            mode.SelectedIndex = Math.Max(0, Array.IndexOf(DisplayModes.All, cfg.Mode));
            Row("Display mode", mode, true);

            Number(poll, 1, 86400, 0, cfg.PollSeconds);
            Row("Update every (seconds)", poll, false);
            Number(hours, 0.1m, 720, 1, (decimal)cfg.HistoryHours);
            Row("Graph time span (hours)", hours, false);
            Number(decimals, -1, 6, 0, cfg.Decimals);
            Row("Decimals (-1 = as reported)", decimals, false);

            min.Text = Show(cfg.Min);
            max.Text = Show(cfg.Max);
            Row("Scale minimum (blank = auto)", min, false);
            Row("Scale maximum (blank = auto)", max, false);

            unit.Text = cfg.Unit;
            Row("Unit (blank = from entity)", unit, false);
            Check(showUnit, "Show unit", cfg.ShowUnit);

            backColor = ColorButton(cfg.BackColor);
            textColor = ColorButton(cfg.TextColor);
            titleColor = ColorButton(string.IsNullOrEmpty(cfg.TitleColor)
                ? Colors.ToHex(Colors.DefaultTitle(textColor.BackColor, backColor.BackColor))
                : cfg.TitleColor);
            entityColor = ColorButton(string.IsNullOrEmpty(cfg.EntityColor) ? Colors.ToHex(titleColor.BackColor) : cfg.EntityColor);
            accentColor = ColorButton(cfg.AccentColor);
            Row("Background colour", backColor, false);
            Row("Title colour", titleColor, false);
            Row("Entity name colour", entityColor, false);
            Row("Value colour", textColor, false);
            Row("Accent colour (graph / gauge)", accentColor, false);

            Number(opacity, 20, 100, 0, cfg.Opacity);
            Row("Opacity (%)", opacity, false);
            Check(topMost, "Always on top", cfg.TopMost);

            Button cancel = AddButton("Cancel");
            Button apply = AddButton("Apply");
            ok = AddButton("OK");
            cancel.DialogResult = DialogResult.Cancel;
            apply.Click += (s, e) => Apply();
            ok.Click += (s, e) => { if (Apply()) DialogResult = DialogResult.OK; };
            AcceptButton = ok;
            CancelButton = cancel;
            FinishLayout();
        }

        void Check(CheckBox box, string text, bool value)
        {
            box.Text = text;
            box.AutoSize = true;
            box.Checked = value;
            Row("", box, false);
        }

        void Number(NumericUpDown n, decimal lo, decimal hi, int places, decimal value)
        {
            n.Minimum = lo;
            n.Maximum = hi;
            n.DecimalPlaces = places;
            n.Value = Math.Max(lo, Math.Min(hi, value));
            n.Width = U(90);
        }

        Button ColorButton(string hex)
        {
            var b = new Button();
            b.FlatStyle = FlatStyle.Flat;
            b.Size = new Size(U(110), U(26));
            b.BackColor = Colors.Parse(hex, Color.Gray);
            b.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog())
                {
                    dlg.Color = b.BackColor;
                    dlg.FullOpen = true;
                    if (dlg.ShowDialog(this) == DialogResult.OK) b.BackColor = dlg.Color;
                }
            };
            return b;
        }

        static string Show(double? v)
        {
            return v.HasValue ? v.Value.ToString(CultureInfo.CurrentCulture) : "";
        }

        static bool TryRead(TextBox box, out double? result)
        {
            result = null;
            string s = box.Text.Trim();
            if (s.Length == 0) return true;
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                result = v;
                return true;
            }
            return false;
        }

        bool Apply()
        {
            double? lo, hi;
            entities.EndEdit();     // a name still being typed counts
            if (selected.Count == 0) return Complain("Choose a device and tick at least one of its entities.", entities);
            if (!TryRead(min, out lo)) return Complain("Scale minimum is not a number.", min);
            if (!TryRead(max, out hi)) return Complain("Scale maximum is not a number.", max);
            if (lo.HasValue && hi.HasValue && lo.Value >= hi.Value) return Complain("Scale minimum must be below the maximum.", min);

            cfg.EntityId = selected[0];
            cfg.ExtraEntities = selected.GetRange(1, selected.Count - 1);
            cfg.Labels = new Dictionary<string, string>();
            foreach (string id in selected)
                if (labels.ContainsKey(id)) cfg.Labels[id] = labels[id];
            // Remembered so the widget can drop the device name from entity names.
            var chosen = device.SelectedItem as DeviceItem;
            if (chosen != null) cfg.DeviceName = chosen == allDevices || chosen == noDevice ? "" : chosen.Name;
            cfg.Title = title.Text.Trim();
            cfg.ShowTitle = showTitle.Checked;
            cfg.Mode = DisplayModes.All[mode.SelectedIndex];
            cfg.PollSeconds = (int)poll.Value;
            cfg.HistoryHours = (double)hours.Value;
            cfg.Decimals = (int)decimals.Value;
            cfg.Min = lo;
            cfg.Max = hi;
            cfg.Unit = unit.Text.Trim();
            cfg.ShowUnit = showUnit.Checked;
            cfg.BackColor = Colors.ToHex(backColor.BackColor);
            cfg.TitleColor = Colors.ToHex(titleColor.BackColor);
            cfg.EntityColor = Colors.ToHex(entityColor.BackColor);
            cfg.TextColor = Colors.ToHex(textColor.BackColor);
            cfg.AccentColor = Colors.ToHex(accentColor.BackColor);
            cfg.Opacity = (int)opacity.Value;
            cfg.TopMost = topMost.Checked;

            if (Applied != null) Applied();
            return true;
        }

        bool Complain(string message, Control focus)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            focus.Focus();
            return false;
        }

        class DeviceItem
        {
            public readonly string Name;
            public readonly List<string> Entities = new List<string>();
            public DeviceItem(string name) { Name = name; }
            public override string ToString() { return Name; }
        }

        void DeviceNote(string note)
        {
            device.Items.Clear();
            device.Items.Add(note);
            device.SelectedIndex = 0;
            device.Enabled = false;
        }

        // Fills the device and entity lists in the background; typing an entity
        // id by hand always works, including when the lists cannot be loaded.
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!Hass.IsConfigured) { DeviceNote("(not connected)"); return; }
            try
            {
                List<EntityInfo> catalog = await Hass.GetEntityCatalog();
                if (IsDisposed) return;
                FillDevices(catalog);
            }
            catch (Exception)
            {
                if (!IsDisposed) DeviceNote("(device list unavailable)");
            }
        }

        void FillDevices(List<EntityInfo> catalog)
        {
            allDevices = new DeviceItem("(All devices)");
            noDevice = new DeviceItem("(No device)");
            var byId = new Dictionary<string, DeviceItem>();
            foreach (EntityInfo info in catalog)
            {
                allDevices.Entities.Add(info.Id);
                DeviceItem item = noDevice;
                if (info.DeviceId.Length > 0 && !byId.TryGetValue(info.DeviceId, out item))
                {
                    item = new DeviceItem(info.DeviceName.Length > 0 ? info.DeviceName : "(unnamed device)");
                    byId[info.DeviceId] = item;
                }
                item.Entities.Add(info.Id);
            }
            if (byId.Count == 0)
            {
                // No device information: behave as a plain entity list.
                DeviceNote("(no devices found)");
                ShowEntities(allDevices);
                return;
            }

            var devices = new List<DeviceItem>(byId.Values);
            devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            if (noDevice.Entities.Count > 0) devices.Add(noDevice);

            // Open on the device that owns all of the widget's entities, if one does.
            DeviceItem pick = allDevices;
            if (selected.Count > 0)
                foreach (DeviceItem d in devices)
                    if (selected.TrueForAll(d.Entities.Contains)) pick = d;

            device.BeginUpdate();
            device.Items.Clear();
            device.Items.Add(allDevices);
            device.Items.AddRange(devices.ToArray());
            device.EndUpdate();
            device.Enabled = true;
            device.SelectedItem = pick;
        }

        // Switches the entity list to one device. Ticks that belong to another
        // device are dropped; "(All devices)" keeps them and allows mixing.
        void ShowEntities(DeviceItem d)
        {
            current = d;
            if (d != allDevices)
            {
                selected.RemoveAll(id => !d.Entities.Contains(id));
                if (selected.Count == 0 && d.Entities.Count == 1) selected.Add(d.Entities[0]);
            }
            if (filter.Text.Length > 0) filter.Text = "";   // refreshes through TextChanged
            else RefreshEntities();
        }

        // Ticked entities first (they stay visible whatever the filter says),
        // then the rest of the current device that match the filter.
        void RefreshEntities()
        {
            string f = filter.Text.Trim();
            var rows = new List<DataGridViewRow>();
            foreach (string id in selected) rows.Add(EntityRow(id, true));
            if (current != null)
            {
                var ticked = new HashSet<string>(selected);
                foreach (string id in current.Entities)
                    if (!ticked.Contains(id) && (f.Length == 0 || id.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                        rows.Add(EntityRow(id, false));
            }
            filling = true;
            entities.Rows.Clear();
            entities.Rows.AddRange(rows.ToArray());
            filling = false;
            add.Enabled = CanAddTyped();
        }

        DataGridViewRow EntityRow(string id, bool ticked)
        {
            string name;
            labels.TryGetValue(id, out name);
            var row = new DataGridViewRow();
            row.CreateCells(entities, ticked, id, name ?? "");
            return row;
        }

        // An id typed by hand, for when it is not in the list (or the list could not be loaded).
        bool CanAddTyped()
        {
            string f = filter.Text.Trim();
            return f.IndexOf('.') > 0 && f.IndexOf(' ') < 0 && !selected.Contains(f);
        }

        void AddTyped()
        {
            if (!CanAddTyped()) return;
            selected.Add(filter.Text.Trim());
            filter.Text = "";
            SuggestTitle();
        }

        // A widget with several entities reads best under its device's name.
        void SuggestTitle()
        {
            var d = device.SelectedItem as DeviceItem;
            if (selected.Count == 2 && title.Text.Trim().Length == 0 && d != null && d != allDevices && d != noDevice)
                title.Text = d.Name;
        }
    }
}
