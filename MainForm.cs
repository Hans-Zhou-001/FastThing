using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FastThing
{
    public sealed class MainForm : Form
    {
        #region Win32 helpers

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        private const uint SHGFI_ICON        = 0x100;
        private const uint SHGFI_SMALLICON   = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_TYPENAME    = 0x400;
        private const uint FILE_ATTRIBUTE_NORMAL    = 0x80;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ShellExecuteW(IntPtr hwnd, string lpOperation,
            string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

        #endregion

        // -------------------------------------------------------------------
        // Controls
        // -------------------------------------------------------------------
        private MenuStrip    _menu       = null!;
        private ToolStrip    _toolbar    = null!;
        private TextBox      _searchBox  = null!;
        private ListView     _listView   = null!;
        private StatusStrip  _status     = null!;
        private ToolStripStatusLabel _statusLabel  = null!;
        private ToolStripStatusLabel _countLabel   = null!;

        // Toolbar buttons (checkable)
        private ToolStripButton _btnCase  = null!;
        private ToolStripButton _btnWholeWord = null!;
        private ToolStripButton _btnRegex = null!; // placeholder, regex not yet impl

        // -------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------
        private readonly SearchEngine _engine = new();
        private CancellationTokenSource _searchCts = new();
        private List<FileRecord> _currentResults = new();
        private ImageList _imageList = null!;

        // Column indices
        private const int COL_NAME = 0;
        private const int COL_PATH = 1;
        private const int COL_SIZE = 2;
        private const int COL_DATE = 3;

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------
        public MainForm()
        {
            InitializeComponent();
            WireEvents();
            _engine.Initialize();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            Text     = "FastThing";
            MinimumSize = new Size(640, 400);
            Size     = new Size(1100, 680);
            Font     = new Font("Segoe UI", 9f, FontStyle.Regular);
            StartPosition = FormStartPosition.CenterScreen;

            // Icon from shell (explorer.exe icon)
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---- Image list for file type icons ----
            _imageList = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };

            // ---- Menu ----
            _menu = new MenuStrip();
            BuildMenu();
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            // ---- Toolbar ----
            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                Padding    = new Padding(4, 2, 4, 2)
            };
            BuildToolbar();
            Controls.Add(_toolbar);

            // ---- Search box ----
            _searchBox = new TextBox
            {
                Dock      = DockStyle.Top,
                Font      = new Font("Segoe UI", 11f),
                Padding   = new Padding(4),
                Height    = 30
            };
            Controls.Add(_searchBox);

            // ---- ListView ----
            _listView = new ListView
            {
                Dock         = DockStyle.Fill,
                View         = View.Details,
                FullRowSelect= true,
                GridLines    = false,
                MultiSelect  = true,
                HideSelection= false,
                SmallImageList = _imageList,
                Font         = new Font("Segoe UI", 9f),
                VirtualMode  = true
            };

            _listView.Columns.Add("名称",         280);
            _listView.Columns.Add("路径",         420);
            _listView.Columns.Add("大小",          90, HorizontalAlignment.Right);
            _listView.Columns.Add("修改日期",      150);

            Controls.Add(_listView);

            // ---- Status bar ----
            _status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel
            {
                Spring    = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _countLabel = new ToolStripStatusLabel("共 0 个对象");
            _status.Items.Add(_statusLabel);
            _status.Items.Add(_countLabel);
            Controls.Add(_status);

            // Control Z-order (top→bottom in tab order)
            _status.BringToFront();
            _listView.BringToFront();
            _searchBox.BringToFront();
            _toolbar.BringToFront();
            _menu.BringToFront();

            ResumeLayout(false);
            PerformLayout();
        }

        // -------------------------------------------------------------------
        // Menu
        // -------------------------------------------------------------------
        private void BuildMenu()
        {
            var file  = new ToolStripMenuItem("文件(&F)");
            var search= new ToolStripMenuItem("搜索(&S)");
            var view  = new ToolStripMenuItem("查看(&V)");
            var tools = new ToolStripMenuItem("工具(&T)");
            var help  = new ToolStripMenuItem("帮助(&H)");

            // File
            var mnuExit = new ToolStripMenuItem("退出(&X)", null, (_, _) => Close());
            mnuExit.ShortcutKeys = Keys.Alt | Keys.F4;
            file.DropDownItems.Add(mnuExit);

            // Search
            var mnuMatchCase  = new ToolStripMenuItem("区分大小写(&C)") { CheckOnClick = true };
            var mnuWholeWord  = new ToolStripMenuItem("全字匹配(&W)")  { CheckOnClick = true };
            var mnuFocus      = new ToolStripMenuItem("聚焦搜索框(&F)", null,
                (_, _) => { _searchBox.Focus(); _searchBox.SelectAll(); });
            mnuFocus.ShortcutKeys = Keys.Control | Keys.F;
            search.DropDownItems.AddRange(new ToolStripItem[]
                { mnuMatchCase, mnuWholeWord, new ToolStripSeparator(), mnuFocus });

            // Tools → Rebuild index
            var mnuRebuild = new ToolStripMenuItem("立即重建索引(&R)", null, OnRebuildIndex);
            var mnuOpenDir = new ToolStripMenuItem("打开索引目录(&O)", null,
                (_, _) => OpenFolder(Path.GetDirectoryName(IndexStore.GetIndexPath())!));
            tools.DropDownItems.AddRange(new ToolStripItem[] { mnuRebuild, mnuOpenDir });

            // Help
            var mnuAbout = new ToolStripMenuItem("关于(&A)", null, OnAbout);
            help.DropDownItems.Add(mnuAbout);

            _menu.Items.AddRange(new ToolStripItem[] { file, search, view, tools, help });

            // Sync menu checkboxes with toolbar
            mnuMatchCase.CheckedChanged  += (_, _) => { _btnCase.Checked = mnuMatchCase.Checked; TriggerSearch(); };
            mnuWholeWord.CheckedChanged  += (_, _) => { _btnWholeWord.Checked = mnuWholeWord.Checked; TriggerSearch(); };
        }

        private void BuildToolbar()
        {
            _btnCase = new ToolStripButton("Aa") { CheckOnClick = true, ToolTipText = "区分大小写" };
            _btnWholeWord = new ToolStripButton("\"W\"") { CheckOnClick = true, ToolTipText = "全字匹配" };
            _btnRegex = new ToolStripButton(".*") { CheckOnClick = true, ToolTipText = "正则表达式（开发中）", Enabled = false };

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("搜索选项："),
                _btnCase,
                _btnWholeWord,
                _btnRegex,
                new ToolStripSeparator(),
                new ToolStripButton("重建索引", null, OnRebuildIndex) { ToolTipText = "立即重建文件索引" }
            });

            _btnCase.CheckedChanged  += (_, _) => TriggerSearch();
            _btnWholeWord.CheckedChanged += (_, _) => TriggerSearch();
        }

        // -------------------------------------------------------------------
        // Event wiring
        // -------------------------------------------------------------------
        private void WireEvents()
        {
            _searchBox.TextChanged  += (_, _) => TriggerSearch();
            _searchBox.KeyDown      += OnSearchBoxKeyDown;

            _listView.RetrieveVirtualItem += OnRetrieveVirtualItem;
            _listView.DoubleClick          += OnListViewDoubleClick;
            _listView.KeyDown              += OnListViewKeyDown;
            _listView.ColumnClick          += OnColumnClick;
            _listView.MouseUp              += OnListViewMouseUp;

            _engine.StatusChanged      += msg => SafeInvoke(() => _statusLabel.Text = msg);
            _engine.IndexReadyChanged  += _ => SafeInvoke(() =>
            {
                _statusLabel.Text = $"索引就绪，共 {_engine.RecordCount:N0} 条记录";
                TriggerSearch();
            });

            Load += (_, _) => _searchBox.Focus();
        }

        // -------------------------------------------------------------------
        // Search trigger (debounced via CancellationToken)
        // -------------------------------------------------------------------
        private System.Windows.Forms.Timer? _debounceTimer;
        private void TriggerSearch()
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _debounceTimer.Tick += (_, _) =>
            {
                _debounceTimer.Stop();
                ExecuteSearch();
            };
            _debounceTimer.Start();
        }

        private void ExecuteSearch()
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
            _searchCts = new CancellationTokenSource();

            string query = _searchBox.Text;
            bool matchCase = _btnCase.Checked;
            bool wholeWord = _btnWholeWord.Checked;
            var token = _searchCts.Token;

            if (string.IsNullOrWhiteSpace(query))
            {
                _currentResults = new List<FileRecord>();
                SafeInvoke(() =>
                {
                    _listView.VirtualListSize = 0;
                    _countLabel.Text = "共 0 个对象";
                });
                return;
            }

            _engine.Search(query, matchCase, wholeWord, results =>
            {
                if (token.IsCancellationRequested) return;
                SafeInvoke(() =>
                {
                    _currentResults = results;
                    _listView.VirtualListSize = results.Count;
                    _countLabel.Text = $"共 {results.Count:N0} 个对象";
                });
            }, token);
        }

        // -------------------------------------------------------------------
        // VirtualMode ListView retrieval
        // -------------------------------------------------------------------
        private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _currentResults.Count)
            {
                e.Item = new ListViewItem();
                return;
            }

            var r = _currentResults[e.ItemIndex];
            string sizeStr = r.IsDirectory ? "" : FormatSize(r.Size);
            string dateStr = r.DateModified == default ? "" : r.DateModified.ToString("yyyy-MM-dd HH:mm");
            string dir     = Path.GetDirectoryName(r.FullPath) ?? r.FullPath;

            var item = new ListViewItem(r.Name);
            item.SubItems.Add(dir);
            item.SubItems.Add(sizeStr);
            item.SubItems.Add(dateStr);

            // Use generic icon (avoid per-item shell calls to keep scrolling fast)
            int imgIdx = r.IsDirectory ? GetDirIconIndex() : GetFileIconIndex(r.Name);
            item.ImageIndex = imgIdx;

            e.Item = item;
        }

        // -------------------------------------------------------------------
        // Icon cache
        // -------------------------------------------------------------------
        private readonly Dictionary<string, int> _iconCache = new();
        private int _dirIconIndex = -1;

        private int GetDirIconIndex()
        {
            if (_dirIconIndex >= 0) return _dirIconIndex;
            _dirIconIndex = AddShellIcon("__folder__", true);
            return _dirIconIndex;
        }

        private int GetFileIconIndex(string filename)
        {
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".__noext__";
            if (_iconCache.TryGetValue(ext, out int idx)) return idx;
            idx = AddShellIcon(ext, false);
            _iconCache[ext] = idx;
            return idx;
        }

        private int AddShellIcon(string key, bool isDir)
        {
            try
            {
                var info = new SHFILEINFO();
                uint attrs = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                string fakePath = isDir ? "C:\\folder" : ("C:\\file" + key);
                SHGetFileInfo(fakePath, attrs, ref info,
                    (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (info.hIcon != IntPtr.Zero)
                {
                    using var ico = Icon.FromHandle(info.hIcon);
                    _imageList.Images.Add(key, (Icon)ico.Clone());
                    DestroyIcon(info.hIcon);
                    return _imageList.Images.Count - 1;
                }
            }
            catch { }
            return -1;
        }

        // -------------------------------------------------------------------
        // Sort state
        // -------------------------------------------------------------------
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        private void OnColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (_sortColumn == e.Column)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = e.Column;
                _sortAscending = true;
            }

            _currentResults.Sort((a, b) =>
            {
                int cmp = _sortColumn switch
                {
                    COL_NAME => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                    COL_PATH => string.Compare(
                        Path.GetDirectoryName(a.FullPath),
                        Path.GetDirectoryName(b.FullPath), StringComparison.OrdinalIgnoreCase),
                    COL_SIZE => a.Size.CompareTo(b.Size),
                    COL_DATE => a.DateModified.CompareTo(b.DateModified),
                    _ => 0
                };
                return _sortAscending ? cmp : -cmp;
            });

            _listView.Invalidate();
        }

        // -------------------------------------------------------------------
        // Open / context menu
        // -------------------------------------------------------------------
        private void OnListViewDoubleClick(object? sender, EventArgs e)
        {
            OpenSelected();
        }

        private void OnListViewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)  OpenSelected();
            if (e.KeyCode == Keys.Delete) { /* no-op for safety */ }
        }

        private void OpenSelected()
        {
            if (_listView.SelectedIndices.Count == 0) return;
            var r = _currentResults[_listView.SelectedIndices[0]];
            try { Process.Start(new ProcessStartInfo(r.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OnListViewMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _listView.HitTest(e.Location);
            if (hit.Item == null) return;

            int idx = hit.Item.Index;
            if (idx < 0 || idx >= _currentResults.Count) return;
            var r = _currentResults[idx];

            var ctx = new ContextMenuStrip();
            ctx.Items.Add("打开", null, (_, _) =>
                { try { Process.Start(new ProcessStartInfo(r.FullPath) { UseShellExecute = true }); } catch { } });
            ctx.Items.Add("打开所在文件夹", null, (_, _) => OpenFolder(Path.GetDirectoryName(r.FullPath)!));
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("复制路径", null, (_, _) => Clipboard.SetText(r.FullPath));
            ctx.Items.Add("复制名称", null, (_, _) => Clipboard.SetText(r.Name));
            ctx.Show(Cursor.Position);
        }

        private static void OpenFolder(string path)
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }

        private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down)
            {
                _listView.Focus();
                if (_listView.VirtualListSize > 0)
                {
                    _listView.SelectedIndices.Clear();
                    _listView.SelectedIndices.Add(0);
                    _listView.EnsureVisible(0);
                }
                e.Handled = true;
            }
        }

        // -------------------------------------------------------------------
        // Rebuild index
        // -------------------------------------------------------------------
        private void OnRebuildIndex(object? sender, EventArgs e)
        {
            _statusLabel.Text = "正在重建索引，请稍候...";
            _engine.ForceRebuild();
        }

        // -------------------------------------------------------------------
        // About
        // -------------------------------------------------------------------
        private void OnAbout(object? sender, EventArgs e)
        {
            MessageBox.Show(
                "FastThing v1.0\n\n" +
                "一个快速文件搜索工具，灵感来自 Everything。\n" +
                "使用 NTFS MFT 枚举实现极速索引，\n" +
                "启动时自动加载上次保存的索引，\n" +
                "可即时搜索，同时在后台更新索引。\n\n" +
                "索引文件路径：\n" + IndexStore.GetIndexPath(),
                "关于 FastThing",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // -------------------------------------------------------------------
        // Utility
        // -------------------------------------------------------------------
        private static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        private void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
    }
}
