using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{

    internal sealed class MainForm : Form
    {
        private const string NotepadPlusPlusPath = @"D:\Program Files\Notepad++\notepad++.exe";
        private readonly RcloneClient client;
        private readonly BufferedListView list = new BufferedListView();
        private readonly Button add = ButtonOf("新增", UiStyle.Accent, UiStyle.OnAccent);
        private readonly Button edit = ButtonOf("编辑", UiStyle.AccentSoft, UiStyle.AccentText);

        // 主窗口与文件管理合并为左右分栏：左侧连接列表，右侧嵌入文件区（参考同类云存储工具布局）。
        // FixedPanel=Panel1 保持侧栏固定宽度，窗口缩放时只伸缩右侧文件区。
        private readonly SplitContainer mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6, BackColor = UiStyle.Surface, Margin = new Padding(0) };
        private readonly Panel fileHost = new Panel { Dock = DockStyle.Fill, BackColor = UiStyle.Card, Margin = new Padding(0) };
        /// <summary>连接侧栏96DPI宽度，按四枚紧凑按钮一行排满为准；名称列吃满剩余，挂载列只留盘符或“未挂载”。</summary>
        internal const int SideWidth = 260;
        internal static readonly int[] SideMinimumWidths = { 130, 72 };
        internal static readonly int[] SideColumnWeights = { 5, 1 };
        private readonly Label filePlaceholder = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiStyle.TextMuted, Font = new Font("Microsoft YaHei UI", 12F), Text = "选择左侧连接，在此浏览和管理文件" };
        /// <summary>当前嵌入右侧的文件窗口；同一时间只保留一个，切换连接时替换。</summary>
        private OssFileForm embeddedFiles;
        private readonly Button more = ButtonOf("更多 ▾", UiStyle.AccentSoft, UiStyle.AccentText);
        private readonly ContextMenuStrip moreMenu = new ContextMenuStrip { Renderer = new UiStyle.StripeMenuRenderer(), ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 10F) };
        private readonly Button test = ButtonOf("测试", UiStyle.AccentSoft, UiStyle.AccentText);
        private readonly Button mount = ButtonOf("挂载", UiStyle.AccentSoft, UiStyle.AccentText);
        private readonly Button unmount = ButtonOf("卸载本地盘", UiStyle.AccentSoft, UiStyle.AccentText);
        private readonly Button refresh = ButtonOf("刷新", UiStyle.AccentSoft, UiStyle.AccentText);
        private readonly ProgressBar upgradeProgress = new ProgressBar();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer mountTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly Dictionary<string, DateTime> restoreAttempts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private int restoreRunning;
        private readonly bool backgroundMode;
        private readonly FileSystemWatcher configWatcher;
        private readonly bool restoreEnabled;
        private readonly string orderPath;
        // 监测事件合并处理，后台快照回到 UI 后仅修改发生变化的单元格。
        private readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly Dictionary<string, CancellationTokenSource> tests = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> mountedDrives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, MountRecord> mountRecords = new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Label status = new Label { AutoEllipsis = true, ForeColor = UiStyle.TextMuted };
        private readonly string geometryFile;
        private bool refreshing, refreshPending, editorOpen, dragging, upgrading, closeAfterTests;

        /// <summary>构建连接列表和本机管理操作入口。</summary>
        /// <param name="startHidden">是否使用启动恢复模式。</param><param name="connectionClient">连接客户端，可空。</param>
        /// <param name="restore">是否自动恢复挂载；隔离回归测试关闭。</param><param name="savedOrderPath">排序文件路径，可空。</param>
        internal MainForm(bool startHidden = false, RcloneClient connectionClient = null, bool restore = true, string savedOrderPath = null)
        {
            client = connectionClient ?? new RcloneClient();
            restoreEnabled = restore;
            orderPath = savedOrderPath ?? ConnectionOrder.FilePath;
            configWatcher = new FileSystemWatcher(Path.GetDirectoryName(client.ConfigurationPath), Path.GetFileName(client.ConfigurationPath));
            backgroundMode = startHidden;
            Text = "OSS 工作空间"; Icon = LoadIcon(); StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(1000, 620); Size = new Size(1200, 700);
            Font = new Font("Microsoft YaHei UI", 9.5F); KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi;
            UiStyle.ApplyWindow(this);
            geometryFile = WindowGeometry.FilePath();
            WindowGeometry.ApplyTo(this, WindowGeometry.Load(geometryFile));
            list.Dock = DockStyle.Fill; list.Margin = new Padding(0, 4, 0, 0);
            list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.GridLines = false; list.Font = new Font("Microsoft YaHei UI", 11F); list.BackColor = UiStyle.Card; list.ForeColor = UiStyle.Text; list.BorderStyle = BorderStyle.FixedSingle;
            list.Columns.Add("连接名称", 180); list.Columns.Add("挂载", 90);
            // 名称靠左读全名，挂载值（盘符或未挂载）靠右与名称分开，扫一眼即可定位已挂载连接。
            list.Columns[1].TextAlign = HorizontalAlignment.Right;
            UiStyle.SetRowHeight(list, 32);
            // 表头居中、正文整格绘制条纹，继续保留系统选中色、状态色和文本对齐。
            list.OwnerDraw = true;
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { UiStyle.DrawHeader(e); };
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { if (list.View != View.Details) e.DrawDefault = true; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { UiStyle.DrawCell(e, list.HoveredIndex); };
            list.ContextMenuStrip = CreateListMenu();
            list.ShowItemToolTips = true; list.AllowDrop = true;
            list.ItemDrag += delegate(object sender, ItemDragEventArgs e)
            {
                dragging = true;
                try { list.DoDragDrop(e.Item, DragDropEffects.Move); }
                finally { dragging = false; list.InsertionMark.Index = -1; if (refreshPending) { reloadTimer.Stop(); reloadTimer.Start(); } }
            };
            list.DragEnter += ListDragOver; list.DragOver += ListDragOver; list.DragDrop += ListDragDrop;
            // 参考同类云存储工具：左侧为连接侧栏，右侧为当前连接的文件区，单窗口完成管理。
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 6), ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            var primary = ActionBar("primaryActions", new[] { add, edit, test, refresh });
            var secondary = ActionBar("secondaryActions", new[] { mount, unmount, more });
            var sidePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            sidePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            sidePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            sidePanel.Controls.Add(primary, 0, 0); sidePanel.Controls.Add(list, 0, 1); sidePanel.Controls.Add(secondary, 0, 2);
            fileHost.Controls.Add(filePlaceholder);
            mainSplit.Panel1.Controls.Add(sidePanel);
            mainSplit.Panel2.Controls.Add(fileHost);
            upgradeProgress.Dock = DockStyle.Bottom; upgradeProgress.Height = 6; upgradeProgress.Minimum = 0; upgradeProgress.Maximum = 100; upgradeProgress.Visible = false;
            status.Dock = DockStyle.Fill; status.Padding = new Padding(4, 3, 0, 0); status.Text = "选择左侧连接即可在右侧浏览和管理文件。";
            var themeToggle = ButtonOf(UiStyle.IsDark ? "浅色" : "深色", UiStyle.AccentSoft, UiStyle.AccentText);
            themeToggle.Dock = DockStyle.Fill; themeToggle.Margin = new Padding(2, 2, 0, 2);
            themeToggle.Click += delegate { ToggleTheme(); };
            var statusBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            statusBar.Controls.Add(status, 0, 0); statusBar.Controls.Add(themeToggle, 1, 0);
            var footer = new Panel { Dock = DockStyle.Fill }; footer.Controls.Add(statusBar); footer.Controls.Add(upgradeProgress);
            layout.Controls.Add(mainSplit, 0, 0); layout.Controls.Add(footer, 0, 1); Controls.Add(layout);
            UiStyle.BindColumns(list, SideMinimumWidths, SideColumnWeights);
            add.Click += delegate { OpenEditor(null); };
            edit.Click += delegate { var item = SelectedItem(); if (item != null) OpenEditor(item.Name); };

            list.ItemActivate += delegate { LoadEmbeddedFiles(); };
            // 单击只展示连接详情（本地读取配置，无远端请求）；双击才打开右侧文件管理。
            list.SelectedIndexChanged += delegate { ShowConnectionDetails(list.SelectedItems.Count == 1 ? list.SelectedItems[0] : null); };
            list.MouseDown += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Right) return;
                var item = list.GetItemAt(args.X, args.Y);
                list.SelectedItems.Clear();
                if (item != null) item.Selected = true;
            };
            test.Click += delegate { TestSelected(); };
            mount.Click += delegate { MountSelected(); };
            unmount.Click += delegate { UnmountSelected(); };
            refresh.Click += delegate { LoadRows(); };
            more.Click += delegate { moreMenu.Show(more, new Point(8, more.Height + 2)); };
            BuildMoreMenu();
            KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Escape) Close(); };
            mountTimer.Tick += delegate { RefreshMountStatus(); BeginRestoreMissingMounts(); };
            mountTimer.Start();
            reloadTimer.Tick += delegate { reloadTimer.Stop(); LoadRows(); };
            configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            configWatcher.Changed += delegate { ReloadConfigurationAsync(); };
            configWatcher.Created += delegate { ReloadConfigurationAsync(); };
            configWatcher.Renamed += delegate { ReloadConfigurationAsync(); };
            configWatcher.EnableRaisingEvents = true;
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (embeddedFiles != null && embeddedFiles.Busy) { e.Cancel = true; status.Text = "文件任务正在执行，请先完成或取消任务。"; }
                else if (operations.Count > 0 || upgrading) { e.Cancel = true; status.Text = "本机操作正在完成，请稍后关闭。"; }
                else if (tests.Count > 0)
                {
                    e.Cancel = true; closeAfterTests = true; status.Text = "正在取消测试，完成后自动关闭…";
                    foreach (var token in tests.Values) token.Cancel();
                }
            };
            FormClosed += delegate { WindowGeometry.Save(geometryFile, WindowGeometry.Capture(this)); mountTimer.Stop(); mountTimer.Dispose(); reloadTimer.Dispose(); configWatcher.Dispose(); CloseEmbeddedFiles(); foreach (var token in tests.Values) token.Cancel(); };
            if (backgroundMode)
            {
                ShowInTaskbar = false;
                Shown += delegate
                {
                    var exitTimer = new System.Windows.Forms.Timer { Interval = 10000 };
                    exitTimer.Tick += delegate { exitTimer.Stop(); exitTimer.Dispose(); Close(); };
                    exitTimer.Start();
                };
            }
            // 布局完成后才设分栏最小宽度和初始位置；构造期容器未布局，直接设置会抛异常。
            // 左右按1:2比例分配，窗口缩放时保持均匀。
            Shown += delegate
            {
                try
                {
                    mainSplit.Panel1MinSize = SideWidth - 10; mainSplit.Panel2MinSize = 420;
                    mainSplit.SplitterDistance = SideWidth;
                }
                catch (InvalidOperationException) { }
                LoadRows(); BeginRestoreMissingMounts();
            };
            toolTip.SetToolTip(add, "新增一个本机 OSS 连接"); toolTip.SetToolTip(edit, "编辑选中的连接"); toolTip.SetToolTip(test, "只读测试连接和 Bucket 列表"); toolTip.SetToolTip(refresh, "重新读取连接和挂载状态"); toolTip.SetToolTip(mount, "挂载或迁移到本地盘符"); toolTip.SetToolTip(unmount, "只卸载本地关联磁盘，不删除远端文件"); toolTip.SetToolTip(more, "删除连接、日志、配置、路径设置等更多操作");

        }

        /// <summary>创建不换行的响应式按钮分组，主要操作与本地挂载操作分开。</summary>
        /// <param name="name">布局标识。</param><param name="buttons">按钮集合。</param><param name="width">96DPI下按钮最小宽度，窄侧栏用紧凑值。</param><returns>按钮分组。</returns>
        private static FlowLayoutPanel ActionBar(string name, IEnumerable<Button> buttons, int width = 52)
        {
            var bar = new FlowLayoutPanel { Name = name, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 5, 0, 0) };
            foreach (var button in buttons)
            {
                // 固定72像素会把“卸载本地盘”截断，按文字实际宽度取宽才能把侧栏整体收窄。
                var text = TextRenderer.MeasureText(button.Text, button.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                button.Size = new Size(Math.Max(width, text + 16), 34); button.Margin = new Padding(0, 0, 6, 0); bar.Controls.Add(button);
            }
            return bar;
        }

        /// <summary>连接F2只修改显示名称，F5刷新连接及挂载状态。</summary>
        /// <param name="msg">窗口消息。</param><param name="keyData">按键和修饰键。</param><returns>是否已处理。</returns>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F2 && list.SelectedItems.Count == 1) { RenameSelected(); return true; }
            if (keyData == Keys.F5) { LoadRows(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>刷新本机连接列表及当前 rclone 挂载状态。</summary>
        private async void LoadRows() { await RefreshRowsAsync(true); }

        /// <summary>后台读取本地快照，在 UI 线程合并行；保留测试状态、选中项及用户排序。</summary>
        /// <param name="reloadNames">是否重新读取连接配置。</param>
        /// <returns>本次刷新完成的任务。</returns>
        private async Task RefreshRowsAsync(bool reloadNames)
        {
            if (refreshing || dragging) { if (reloadNames) refreshPending = true; return; }
            refreshing = true;
            try
            {
                List<string> names = null;
                Dictionary<string, string> labels = null;
                Dictionary<string, string> mounts = null;
                Dictionary<string, MountRecord> records = null;
                await Task.Run(() =>
                {
                    if (reloadNames) { names = client.GetRemotes(); labels = client.GetDisplayNames(); }
                    mounts = GetMounts(); records = MountStore.Read();
                });
                if (IsDisposed || Disposing) return;
                mountedDrives = mounts; mountRecords = records;
                var structural = names != null && (names.Count != list.Items.Count || names.Any(x => FindRow(x) == null));
                if (structural) list.BeginUpdate();
                try
                {
                    if (names != null)
                    {
                        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                        foreach (var old in list.Items.Cast<ListViewItem>().Where(x => !wanted.Contains(x.Name)).ToList()) list.Items.Remove(old);
                        // 连接被删除或改名后，右侧嵌入的文件区随之关闭，避免继续展示失效连接。
                        if (embeddedFiles != null && !wanted.Contains(embeddedFiles.ConnectionName)) CloseEmbeddedFiles();
                        foreach (var name in ConnectionOrder.Apply(names, ConnectionOrder.Read(orderPath)))
                        {
                            var row = FindRow(name);
                            if (row == null) { row = new ListViewItem(new[] { labels[name], "未挂载" }) { Name = name }; list.Items.Add(row); }
                            else SetCell(row, 0, labels[name]);
                            row.ToolTipText = "内部标识：" + name + "\r\n显示名称可以自由修改，不影响挂载。";
                            if (embeddedFiles != null && String.Equals(embeddedFiles.ConnectionName, name, StringComparison.OrdinalIgnoreCase)) embeddedFiles.SetDisplayName(labels[name]);
                        }
                    }
                    foreach (ListViewItem item in list.Items)
                    {
                        string drive; mountedDrives.TryGetValue(item.Name, out drive);
                        SetCell(item, 1, String.IsNullOrWhiteSpace(drive) ? "未挂载" : drive);
                        var color = UiStyle.StripeColor(item.Index);
                        if (item.BackColor != color) item.BackColor = color;
                    }
                }
                finally { if (structural) list.EndUpdate(); }
            }
            catch (Exception ex) { UiStyle.SetStatus(status, "刷新失败，已保留现有列表：" + ex.Message, "error"); ManagerLog.Write("刷新失败", "", "", ex.Message); }
            finally
            {
                refreshing = false;
                if (refreshPending && !IsDisposed) { refreshPending = false; reloadTimer.Stop(); reloadTimer.Start(); }
            }
        }

        private void OpenEditor(string name)
        {
            if (upgrading || editorOpen || (name != null && operations.Contains(name))) return;
            editorOpen = true;
            try
            {
                using (var editor = new EditorForm(client, name, mountedDrives, mountRecords))
                    if (editor.ShowDialog(this) == DialogResult.OK)
                    {
                        // 编辑中的连接配置已变化，关闭右侧文件区避免继续使用旧配置浏览。
                        if (name != null && embeddedFiles != null && String.Equals(embeddedFiles.ConnectionName, name, StringComparison.OrdinalIgnoreCase)) CloseEmbeddedFiles();
                        LoadRows();
                    }
            }
            catch (Exception ex) { status.Text = "打开编辑页失败：" + ex.Message; }
            finally { editorOpen = false; }
        }
        /// <summary>在右侧嵌入当前连接的文件管理区；同一时间只保留一个，切换连接时替换。</summary>
        /// <param name="name">连接名称；为空时取当前选中的行。</param>
        private bool loadingEmbedded;
        private string pendingEmbedded;
        private async void LoadEmbeddedFiles(string name = null)
        {
            if (name == null)
            {
                var item = list.SelectedItems.Count == 1 ? list.SelectedItems[0] : null;
                name = item == null ? null : item.Name;
            }
            if (name == null || upgrading || operations.Contains(name)) return;
            if (embeddedFiles != null && String.Equals(embeddedFiles.ConnectionName, name, StringComparison.OrdinalIgnoreCase)) return;
            if (embeddedFiles != null && embeddedFiles.Busy) { status.Text = "文件任务正在执行，完成后再切换连接。"; return; }
            // 连接元数据走后台线程读取，避免远端响应慢时同步卡死界面；加载期间再点的连接记为最新请求，完成后接着切换。
            if (loadingEmbedded) { pendingEmbedded = name; return; }
            loadingEmbedded = true;
            UiStyle.SetStatus(status, name + "：正在打开文件管理…", "info");
            try
            {
                var metadata = await Task.Run(() => client.GetMetadata(name));
                if (IsDisposed || Disposing) return;
                if (FindRow(name) == null) return;
                if (embeddedFiles != null && !embeddedFiles.IsDisposed && embeddedFiles.Busy) { UiStyle.SetStatus(status, "文件任务正在执行，完成后再切换连接。", "warn"); return; }
                CloseEmbeddedFiles();
                string type; metadata.TryGetValue("type", out type);
                string drive; mountedDrives.TryGetValue(name, out drive);
                var window = new OssFileForm(client, name, String.Equals(type, "s3", StringComparison.OrdinalIgnoreCase), drive, true, true) { Dock = DockStyle.Fill };
                string label; metadata.TryGetValue("description", out label); window.SetDisplayName(String.IsNullOrWhiteSpace(label) ? name : label);
                window.FormClosed += delegate { if (embeddedFiles == window) embeddedFiles = null; filePlaceholder.Visible = true; };
                embeddedFiles = window;
                filePlaceholder.Visible = false;
                fileHost.Controls.Add(window);
                window.Show();
                UiStyle.SetStatus(status, name + "：文件管理已就绪。", "success");
            }
            catch (Exception) { if (!IsDisposed) { UiStyle.SetStatus(status, "打开文件管理失败，请检查连接配置。", "error"); filePlaceholder.Visible = true; } }
            finally
            {
                loadingEmbedded = false;
                if (!IsDisposed && pendingEmbedded != null)
                {
                    var next = pendingEmbedded; pendingEmbedded = null;
                    if (embeddedFiles == null || !String.Equals(embeddedFiles.ConnectionName, next, StringComparison.OrdinalIgnoreCase)) LoadEmbeddedFiles(next);
                }
            }
        }

        /// <summary>关闭右侧嵌入的文件区并恢复占位提示；切换连接、编辑或删除连接时调用。</summary>
        private void CloseEmbeddedFiles()
        {
            var window = embeddedFiles;
            embeddedFiles = null;
            if (window != null && !window.IsDisposed) window.Dispose();
            filePlaceholder.Visible = true;
        }

        /// <summary>单击连接时在右侧占位区显示只读配置摘要；只挑协议、地址等非秘密字段，不含密钥与口令；本机配置读不到时说明原因而不是留空。</summary>
        /// <param name="item">选中的连接行，空选择时恢复默认占位文案。</param>
        private void ShowConnectionDetails(ListViewItem item)
        {
            if (item == null) { filePlaceholder.Text = "选择左侧连接，在此浏览和管理文件"; return; }
            string type = "", endpoint = "", provider = "", region = "", description = "", unreadable = "";
            try
            {
                var metadata = client.GetMetadata(item.Name);
                metadata.TryGetValue("type", out type);
                metadata.TryGetValue("provider", out provider);
                metadata.TryGetValue("region", out region);
                metadata.TryGetValue("description", out description);
                metadata.TryGetValue(type == "webdav" ? "url" : "endpoint", out endpoint);
            }
            catch (Exception) { unreadable = "本机连接配置暂时读不到，请确认配置文件未被其他程序占用。"; }
            string drive; mountedDrives.TryGetValue(item.Name, out drive);
            var lines = new List<string> { String.IsNullOrWhiteSpace(item.Text) ? item.Name : item.Text, "" };
            if (type != "") lines.Add("类型：" + type);
            if (endpoint != "") lines.Add("地址：" + endpoint);
            if (provider != "") lines.Add("提供商：" + provider);
            if (region != "") lines.Add("区域：" + region);
            if (description != "") lines.Add("备注：" + description);
            lines.Add("挂载：" + (String.IsNullOrWhiteSpace(drive) ? "未挂载" : drive));
            if (unreadable != "") lines.Add(unreadable);
            lines.Add("");
            lines.Add("双击连接打开文件管理");
            filePlaceholder.Text = String.Join("\n", lines);
        }

        /// <summary>根据连接名称查找当前行，不持有刷新前的过期行引用。</summary>
        /// <param name="name">连接名称。</param><returns>当前行或空值。</returns>
        private ListViewItem FindRow(string name) { return list.Items.Cast<ListViewItem>().FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); }
        /// <summary>仅在文本改变时重绘单元格。</summary>
        /// <param name="item">目标行。</param><param name="index">列号。</param><param name="text">新文本。</param>
        private static void SetCell(ListViewItem item, int index, string text) { if (item.SubItems[index].Text != text) item.SubItems[index].Text = text; }
        private string SelectedName() { return list.SelectedItems.Count == 1 ? list.SelectedItems[0].Name : null; }
        private ListViewItem SelectedItem() { if (list.SelectedItems.Count != 1) { MessageBox.Show("请先选择一个连接。", "OSS 管理器", MessageBoxButtons.OK, MessageBoxIcon.Information); return null; } return list.SelectedItems[0]; }
        /// <summary>独立修改显示名称；文件窗口打开或连接已挂载时也无需卸载。</summary>
        private void RenameSelected()
        {
            var item = SelectedItem(); if (item == null || upgrading || operations.Contains(item.Name)) return;
            using (var dialog = new Form { Text = "修改连接名称", ClientSize = new Size(520, 190), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, Font = Font })
            {
                UiStyle.ApplyWindow(dialog);
                var hint = new Label { Text = "支持中文、数字、空格和常见符号。只改名称，不影响现有挂载。", Location = new Point(20, 18), Size = new Size(480, 32), ForeColor = UiStyle.TextMuted };
                var input = new TextBox { Text = item.Text, Location = new Point(20, 58), Width = 480 };
                var ok = ButtonOf("保存名称", UiStyle.Accent, UiStyle.OnAccent); ok.SetBounds(280, 120, 104, 36); ok.DialogResult = DialogResult.OK;
                var back = ButtonOf("取消", UiStyle.AccentSoft, UiStyle.AccentText); back.SetBounds(396, 120, 104, 36); back.DialogResult = DialogResult.Cancel;
                dialog.Controls.AddRange(new Control[] { hint, input, ok, back }); dialog.AcceptButton = ok; dialog.CancelButton = back;
                if (dialog.ShowDialog(this) != DialogResult.OK || input.Text.Trim() == item.Text) return;
                var label = input.Text.Trim(); var name = item.Name;
                RunOperation(name, "修改显示名称", () => client.UpdateDisplayName(name, label));
            }
        }
        /// <summary>测试当前选中连接并将结果显示在列表中。</summary>
        private async void TestSelected()
        {
            if (upgrading) { status.Text = "升级期间暂不启动连接测试。"; return; }
            var item = SelectedItem(); if (item == null || operations.Contains(item.Name)) return;
            var name = item.Name;
            CancellationTokenSource previous;
            if (tests.TryGetValue(name, out previous)) { previous.Cancel(); return; }
            var cancellation = new CancellationTokenSource(); tests.Add(name, cancellation);
            UiStyle.SetStatus(status, name + "：正在测试连接，再点一次可取消。", "info");
            string message;
            try { await Task.Run(() => RcloneClient.EnsureSuccess(client.Test(name, cancellation.Token), "连接测试失败")); message = "连接正常"; }
            catch (OperationCanceledException) { message = "测试已取消"; }
            catch (Exception ex) { message = ex.Message; ManagerLog.Write("连接测试失败", name, "", message); }
            finally { tests.Remove(name); cancellation.Dispose(); }
            if (IsDisposed) return;
            if (closeAfterTests && tests.Count == 0) { Close(); return; }
            item = FindRow(name);
            if (item != null) item.ToolTipText = message;
            UiStyle.SetStatus(status, name + "：" + message, message == "连接正常" ? "success" : message == "测试已取消" ? "" : "error");
        }
        /// <summary>请求盘符并后台挂载当前选中连接；磁盘名称预填本机记录，未挂载过才用显示名。</summary>
        private void MountSelected()
        {
            var item = SelectedItem(); if (item == null) return;
            if (operations.Contains(item.Name)) return;
            string currentDrive; mountedDrives.TryGetValue(item.Name, out currentDrive);
            MountRecord record; mountRecords.TryGetValue(item.Name, out record);
            var request = DriveDialog.Ask(this, currentDrive, record == null ? item.Text : record.VolumeName); if (request == null) return;
            var name = item.Name;
            RunOperation(name, "挂载", () => { string actual; GetMounts().TryGetValue(name, out actual); client.ChangeMount(name, actual, request.Drive, request.VolumeName); });
        }
        /// <summary>卸载当前选中连接对应的盘符。</summary>
        private void UnmountSelected()
        {
            var item = SelectedItem(); if (item == null || operations.Contains(item.Name)) return; string drive; mountedDrives.TryGetValue(item.Name, out drive);
            if (String.IsNullOrWhiteSpace(drive)) { MessageBox.Show("该连接当前未挂载。", "卸载", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var name = item.Name;
            RunOperation(name, "卸载", () => { string actual; if (GetMounts().TryGetValue(name, out actual)) client.Unmount(actual); });
        }
        /// <summary>经确认后仅删除当前选中的本机连接。</summary>
        private void DeleteSelected()
        {
            var item = SelectedItem(); if (item == null || operations.Contains(item.Name)) return;
            if (mountedDrives.ContainsKey(item.Name)) { status.Text = "请先卸载该连接，再删除本机配置。"; return; }
            if (MessageBox.Show("删除本机连接“" + item.Text + "”？\r\n不会删除任何 bucket 或远端文件。", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var name = item.Name;
            RunOperation(name, "删除连接", () => client.Delete(name));
        }
        /// <summary>后台执行本机更改，同一连接禁止重复点击，其他连接仍可测试。</summary>
        /// <param name="name">连接名称。</param><param name="action">操作说明。</param><param name="work">后台操作。</param>
        private async void RunOperation(string name, string action, Action work)
        {
            if (upgrading) { status.Text = "升级期间暂不执行连接更改。"; return; }
            if (!operations.Add(name)) return;
            CancellationTokenSource testToken; if (tests.TryGetValue(name, out testToken)) testToken.Cancel();
            UiStyle.SetStatus(status, name + "：正在" + action + "…", "info");
            try { await Task.Run(() => { lock (RcloneClient.MountSync) work(); }); UiStyle.SetStatus(status, name + "：" + action + "完成", "success"); }
            catch (Exception ex) { UiStyle.SetStatus(status, name + "：" + action + "失败：" + ex.Message, "error"); ManagerLog.Write(action + "失败", name, "", ex.Message); }
            finally { operations.Remove(name); if (!IsDisposed) LoadRows(); }
        }
        /// <summary>用本机 Notepad++ 打开当前 rclone 配置文件，供用户直接维护连接。</summary>
        private void OpenConfigFile()
        {
            if (embeddedFiles != null && embeddedFiles.Busy) { status.Text = "文件任务正在执行，完成后再编辑连接配置。"; return; }
            try
            {
                if (!File.Exists(NotepadPlusPlusPath)) throw new FileNotFoundException("未找到 Notepad++。", NotepadPlusPlusPath);
                Process.Start(new ProcessStartInfo(NotepadPlusPlusPath, "\"" + RcloneClient.ConfigPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("无法打开配置文件：" + ex.Message, "OSS 管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        /// <summary>使用 Notepad++ 打开当前连接的 rclone 文件操作日志。</summary>
        private void OpenSelectedLog()
        {
            var item = SelectedItem(); if (item == null) return;
            try
            {
                if (!File.Exists(NotepadPlusPlusPath)) throw new FileNotFoundException("未找到 Notepad++。", NotepadPlusPlusPath);
                var logPath = RcloneClient.OperationLogPath(item.Name);
                if (!File.Exists(logPath)) File.WriteAllText(logPath, "尚未生成文件操作日志。请先重新挂载该连接后再进行文件操作。" + Environment.NewLine, new UTF8Encoding(false));
                Process.Start(new ProcessStartInfo(NotepadPlusPlusPath, "\"" + logPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("无法打开连接日志：" + ex.Message, "OSS 管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        /// <summary>后台执行官方 rclone 升级，并实时显示下载百分比、大小、速度和阶段。</summary>
        internal async void UpgradeRclone()
        {
            if (upgrading || operations.Count > 0 || tests.Count > 0 || (embeddedFiles != null && embeddedFiles.Busy))
            {
                status.Text = "请先等待当前操作结束、取消测试并完成文件任务，再升级 rclone。"; return;
            }
            upgrading = true; add.Enabled = edit.Enabled = more.Enabled = false;
            UiStyle.SetStatus(status, "正在检查 rclone 版本…", "info");
            try
            {
                var current = await Task.Run(() => client.CurrentVersion());
                var latest = await Task.Run(() => RcloneClient.GetLatestVersion());
                if (!RcloneClient.IsNewerVersion(latest, current)) { status.Text = "当前版本：" + current + "；最新版本：" + latest + "，无需升级。"; return; }
                upgradeProgress.Value = 0; upgradeProgress.Visible = true;
                var completion = String.Empty;
                await Task.Run(() =>
                {
                    lock (RcloneClient.MountSync)
                    {
                        client.Upgrade(latest, (percent, received, total, speed, stage) =>
                        {
                            if (!IsDisposed && IsHandleCreated)
                                BeginInvoke((Action)delegate { if (IsDisposed) return; upgradeProgress.Value = Math.Max(0, Math.Min(100, percent)); status.Text = RcloneClient.FormatDownloadProgress(percent, received, total, speed, stage); });
                        }, message => completion = message);
                    }
                });
                status.Text = completion;
            }
            catch (Exception ex) { UiStyle.SetStatus(status, "升级失败：" + ex.Message, "error"); ManagerLog.Write("升级失败", "", "", ex.Message); }
            finally
            {
                upgrading = false;
                if (!IsDisposed) { add.Enabled = edit.Enabled = more.Enabled = true; upgradeProgress.Visible = false; LoadRows(); }
            }
        }
        internal string GetCurrentVersion() { return client.CurrentVersion(); }
        /// <summary>仅更新已显示连接的挂载状态，避免定时访问远端 OSS。</summary>
        private async void RefreshMountStatus() { await RefreshRowsAsync(false); }
        /// <summary>由管理器统一恢复记录中的缺失挂载，避免依赖外部 VBS 常驻。</summary>
        private void BeginRestoreMissingMounts()
        {
            if (!restoreEnabled || IsDisposed || upgrading) return;
            if (Interlocked.Exchange(ref restoreRunning, 1) != 0) return;
            Task.Run(() =>
            {
                try
                {
                    lock (RcloneClient.MountSync)
                    {
                        var records = MountStore.Read();
                        var active = GetMounts();
                        var occupied = new HashSet<string>(Environment.GetLogicalDrives().Select(x => x.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in records)
                        {
                            if (active.ContainsKey(entry.Key) || occupied.Contains(entry.Value.Drive)) continue;
                            DateTime last; if (restoreAttempts.TryGetValue(entry.Key, out last) && (DateTime.Now - last).TotalSeconds < 30) continue;
                            restoreAttempts[entry.Key] = DateTime.Now;
                            try { client.Mount(entry.Key, entry.Value.Drive, entry.Value.VolumeName); ManagerLog.Write("自动恢复挂载", entry.Key, entry.Value.Drive, "success"); }
                            catch (Exception ex) { ManagerLog.Write("自动恢复挂载失败", entry.Key, entry.Value.Drive, ex.Message); UiStyle.SetStatus(status, "自动恢复挂载失败（" + entry.Key + "）：" + ex.Message, "error"); }
                        }
                    }
                }
                catch (Exception ex) { ManagerLog.Write("检查恢复挂载失败", "", "", ex.Message); UiStyle.SetStatus(status, "检查恢复挂载失败：" + ex.Message, "error"); }
                finally { Interlocked.Exchange(ref restoreRunning, 0); }
            });
        }
        /// <summary>将文件监测事件切回界面线程后重新读取 rclone 配置。</summary>
        private void ReloadConfigurationAsync()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke((Action)delegate { if (IsDisposed) return; reloadTimer.Stop(); reloadTimer.Start(); }); } catch (InvalidOperationException) { }
        }
        internal static Dictionary<string, string> GetMounts()
        {
            return GetMountLocations().ToDictionary(x => x.Key, x => x.Value.Drive, StringComparer.OrdinalIgnoreCase);
        }
        /// <summary>读取当前挂载进程，包括挂载远端子目录时的路径映射。</summary>
        /// <returns>内部连接标识对应的盘符和远端根路径。</returns>
        internal static Dictionary<string, MountedLocation> GetMountLocations()
        {
            var map = new Dictionary<string, MountedLocation>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='rclone.exe'"))
                {
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                foreach (ManagementObject p in searcher.Get())
                {
                    string remote; string drive; string root;
                    if (TryParseMountCommand(Convert.ToString(p["CommandLine"]), out remote, out drive, out root)) map[remote] = new MountedLocation { Drive = drive, Root = root };
                }
                }
            }
            catch { }
            return map;
        }
        /// <summary>解析带引号或不带引号的 rclone mount 命令行，以识别 remote 与盘符。</summary>
        internal static bool TryParseMountCommand(string commandLine, out string remote, out string drive)
        {
            string root;
            return TryParseMountCommand(commandLine, out remote, out drive, out root);
        }
        /// <summary>识别 mount/cmount 的源和盘符，支持前置选项及子目录挂载。</summary>
        /// <param name="commandLine">进程命令行，不写入日志。</param><param name="remote">内部连接标识。</param>
        /// <param name="drive">盘符。</param><param name="root">远端挂载根路径。</param><returns>是否识别成功。</returns>
        internal static bool TryParseMountCommand(string commandLine, out string remote, out string drive, out string root)
        {
            remote = null; drive = null; root = null;
            if (String.IsNullOrWhiteSpace(commandLine)) return false;
            var tokens = System.Text.RegularExpressions.Regex.Matches(commandLine, "\"[^\"]*\"|\\S+").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value.Trim('"')).ToList();
            var mountIndex = tokens.FindIndex(value => value.Equals("mount", StringComparison.OrdinalIgnoreCase) || value.Equals("cmount", StringComparison.OrdinalIgnoreCase));
            if (mountIndex < 0 || tokens.Count < mountIndex + 3) return false;
            for (var i = mountIndex + 1; i + 1 < tokens.Count; i++)
            {
                var value = tokens[i]; var colon = value.IndexOf(':'); var target = tokens[i + 1].TrimEnd('\\');
                if (colon < 1 || value.StartsWith("-", StringComparison.Ordinal) || value.Substring(0, colon).IndexOfAny(new[] { '/', '\\' }) >= 0 || !System.Text.RegularExpressions.Regex.IsMatch(target, "(?i)^[A-Z]:$")) continue;
                if (new[] { "--volname", "--devname", "--log-file", "--config", "--cache-dir" }.Contains(tokens[i - 1], StringComparer.OrdinalIgnoreCase)) continue;
                var path = value.Substring(colon + 1).Trim('/');
                try { OssFileClient.ValidatePath(path); } catch (ArgumentException) { continue; }
                remote = value.Substring(0, colon); drive = target.ToUpperInvariant(); root = path; return true;
            }
            return false;
        }
        internal static Button ButtonOf(string text, Color back, Color fore)
        {
            var button = new FlatButton { Text = text, BackColor = back, ForeColor = fore, Font = new Font("Microsoft YaHei UI", 9.5F), Cursor = Cursors.Hand };
            return button;
        }
        /// <summary>只接收本列表的拖动，并显示插入位置。</summary>
        /// <param name="sender">事件来源。</param><param name="e">拖放数据。</param>
        private void ListDragOver(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            e.Effect = item != null && item.ListView == list ? DragDropEffects.Move : DragDropEffects.None;
            var point = list.PointToClient(new Point(e.X, e.Y));
            var target = list.GetItemAt(point.X, point.Y);
            list.InsertionMark.Index = target == null ? -1 : target.Index;
            list.InsertionMark.AppearsAfterItem = target != null && point.Y > target.Bounds.Top + target.Bounds.Height / 2;
        }
        /// <summary>移动原行并保存顺序，保留连接状态及正在进行的测试。</summary>
        /// <param name="sender">事件来源。</param><param name="e">拖放数据。</param>
        private void ListDragDrop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(ListViewItem)) as ListViewItem;
            if (item == null || item.ListView != list) return;
            var point = list.PointToClient(new Point(e.X, e.Y));
            var target = list.GetItemAt(point.X, point.Y);
            var index = target == null ? list.Items.Count : target.Index + (point.Y > target.Bounds.Top + target.Bounds.Height / 2 ? 1 : 0);
            if (item.Index < index) index--;
            list.InsertionMark.Index = -1;
            if (index == item.Index) return;
            list.BeginUpdate();
            try { list.Items.Remove(item); list.Items.Insert(index, item); item.Selected = true; item.Focused = true; }
            finally { list.EndUpdate(); }
            try { ConnectionOrder.Save(list.Items.Cast<ListViewItem>().Select(x => x.Name), orderPath); UiStyle.SetStatus(status, "列表顺序已保存。", "success"); }
            catch (Exception ex) { UiStyle.SetStatus(status, "本次排序已调整，但保存失败；重开后不会保留。", "error"); ManagerLog.Write("保存排序失败", "", "", ex.Message); }
            RefreshMountStatus();
        }
        /// <summary>创建连接列表的右键快捷操作，并始终复用主界面同一套处理逻辑。</summary>
        private ContextMenuStrip CreateListMenu()
        {
            var menu = new ContextMenuStrip { Renderer = new UiStyle.StripeMenuRenderer(), ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 10F) };
            menu.Items.Add("新增", null, delegate { OpenEditor(null); });
            menu.Items.Add("编辑", null, delegate { var item = SelectedItem(); if (item != null) OpenEditor(item.Name); });
            menu.Items.Add("测试", null, delegate { TestSelected(); });
            menu.Items.Add("改名（不影响挂载）", null, delegate { RenameSelected(); });
            menu.Items.Add("管理文件", null, delegate { LoadEmbeddedFiles(); });
            menu.Items.Add("刷新", null, delegate { LoadRows(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("挂载", null, delegate { MountSelected(); });
            menu.Items.Add("卸载", null, delegate { UnmountSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("查看日志", null, delegate { OpenSelectedLog(); });
            menu.Items.Add(new ToolStripSeparator());
            var delete = new ToolStripMenuItem("删除") { ForeColor = UiStyle.DangerText };
            delete.Click += delegate { DeleteSelected(); };
            menu.Items.Add(delete);
            menu.Opening += delegate
            {
                var selected = SelectedName();
                foreach (ToolStripItem entry in menu.Items)
                {
                    if (entry is ToolStripSeparator) continue;
                    entry.Enabled = !upgrading && (entry.Text == "新增" || entry.Text == "刷新" || (selected != null && !operations.Contains(selected)));
                }
            };
            return menu;
        }
        /// <summary>把低频操作收进“更多”菜单：删除、日志与配置、迁移设置、升级和关于。</summary>
        private void BuildMoreMenu()
        {
            var deleteItem = new ToolStripMenuItem("删除连接") { ForeColor = UiStyle.DangerText };
            deleteItem.Click += delegate { DeleteSelected(); };
            var logItem = new ToolStripMenuItem("查看操作日志");
            logItem.Click += delegate { OpenSelectedLog(); };
            var configItem = new ToolStripMenuItem("编辑 rclone 配置");
            configItem.Click += delegate { OpenConfigFile(); };
            var exportItem = new ToolStripMenuItem("导出连接信息（JSON）…");
            exportItem.Click += delegate { ExportConnections(); };
            var importItem = new ToolStripMenuItem("导入连接信息（JSON）…");
            importItem.Click += delegate { ImportConnections(); };
            var pathsItem = new ToolStripMenuItem("路径设置…");
            pathsItem.Click += delegate { using (var dialog = new RuntimeSettingsForm()) dialog.ShowDialog(this); };
            var themeItem = new ToolStripMenuItem("切换为深色皮肤");
            themeItem.Click += delegate { ToggleTheme(); };
            var upgradeItem = new ToolStripMenuItem("升级 rclone");
            upgradeItem.Click += delegate { UpgradeRclone(); };
            var aboutItem = new ToolStripMenuItem("关于");
            aboutItem.Click += delegate { using (var dialog = new AboutForm(this)) dialog.ShowDialog(this); };
            moreMenu.Items.AddRange(new ToolStripItem[] { deleteItem, new ToolStripSeparator(), logItem, configItem, new ToolStripSeparator(), exportItem, importItem, pathsItem, themeItem, new ToolStripSeparator(), upgradeItem, new ToolStripSeparator(), aboutItem });
            moreMenu.Opening += delegate
            {
                var selected = SelectedName();
                var busy = upgrading || operations.Count > 0;
                deleteItem.Enabled = !busy && selected != null && !operations.Contains(selected);
                logItem.Enabled = !busy && selected != null;
                configItem.Enabled = !busy;
                exportItem.Enabled = !busy;
                importItem.Enabled = !busy;
                pathsItem.Enabled = !busy;
                themeItem.Text = UiStyle.IsDark ? "切换为浅色皮肤" : "切换为深色皮肤";
                themeItem.Enabled = !busy;
                upgradeItem.Enabled = !busy && tests.Count == 0;
                aboutItem.Enabled = !busy;
            };
        }

        /// <summary>把连接信息导出为 JSON，可选择附带账号与密钥明文。</summary>
        private void ExportConnections()
        {
            using (var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "rclone-connections.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var includeSecrets = MessageBox.Show(this, "是否在导出文件中包含账号与密钥明文？\r\n不含密钥时，导入到新电脑会缺少凭证，需要重新填写。", "导出连接信息", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                try
                {
                    var json = ConnectionExport.BuildJson(client, client.GetRemotes(), includeSecrets);
                    File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false));
                    UiStyle.SetStatus(status, "已导出连接信息" + (includeSecrets ? "（含密钥）" : "") + "：" + dialog.FileName, "success");
                }
                catch (Exception ex) { UiStyle.SetStatus(status, "导出连接信息失败：" + ex.Message, "error"); }
            }
        }

        /// <summary>从 JSON 导入连接，按名称新增或更新，导入前备份 rclone.conf。</summary>
        private void ImportConnections()
        {
            using (var dialog = new OpenFileDialog { Filter = "JSON 文件|*.json", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "导入会按名称新增或更新连接，并先备份 rclone.conf 为 .bak。确定继续？", "导入连接信息", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                try
                {
                    var message = ConnectionExport.Import(client, dialog.FileName);
                    LoadRows();
                    UiStyle.SetStatus(status, "已导入连接信息。", "success");
                    MessageBox.Show(this, message, "导入连接信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { UiStyle.SetStatus(status, "导入连接信息失败：" + ex.Message, "error"); }
            }
        }

        /// <summary>切换明暗皮肤并保存设置，随后以新实例重启以应用全部配色。</summary>
        private void ToggleTheme()
        {
            var next = UiStyle.IsDark ? "light" : "dark";
            try { RuntimePaths.Current.Save(RuntimePaths.Current.PersistableExecutable, RuntimePaths.Current.RcloneConfiguration, RuntimePaths.Current.FileManagerExecutable, next); }
            catch (Exception ex) { MessageBox.Show(this, "切换皮肤失败：" + ex.Message, "皮肤", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--theme-restart"));
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, "已保存皮肤设置，请手动重启程序生效。\r\n" + ex.Message, "皮肤", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private static Icon LoadIcon() { return RuntimePaths.LoadIcon(); }
    }
}
