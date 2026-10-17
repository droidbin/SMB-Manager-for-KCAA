using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace SmbManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs error)
                {
                    LogUnhandledException(error.Exception);
                    MessageBox.Show("처리 중 오류가 발생했습니다.\r\n자세한 내용은 crash.log를 확인해 주세요.\r\n\r\n" + error.Exception.Message, "SMB Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs error)
                {
                    LogUnhandledException(error.ExceptionObject as Exception);
                };
                Application.Run(new MainForm(IsWindowsStartupLaunch(args)));
            }
            catch (Exception error)
            {
                try
                {
                    var logDir = Path.Combine(Path.GetTempPath(), "SMB Manager");
                    Directory.CreateDirectory(logDir);
                    File.WriteAllText(Path.Combine(logDir, "startup-error.log"), error.ToString(), Encoding.UTF8);
                }
                catch
                {
                }

                MessageBox.Show(
                    "앱 실행 중 오류가 발생했습니다.\r\n" + error.Message,
                    "SMB Manager",
                    MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            }
        }

        private static bool IsWindowsStartupLaunch(string[] args)
        {
            if (args == null)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "/startup", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "-startup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void LogUnhandledException(Exception error)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMB Manager");
                if (string.IsNullOrWhiteSpace(logDir))
                {
                    logDir = Path.Combine(Path.GetTempPath(), "SMB Manager");
                }

                Directory.CreateDirectory(logDir);
                var text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + (error == null ? "Unknown exception" : error.ToString()) + "\r\n\r\n";
                File.AppendAllText(Path.Combine(logDir, "crash.log"), text, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    internal sealed class CenteredTextButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var rect = ClientRectangle;
            var fillColor = Enabled ? BackColor : SystemColors.Control;
            var textColor = Enabled ? ForeColor : SystemColors.GrayText;

            using (var brush = new SolidBrush(fillColor))
            {
                e.Graphics.FillRectangle(brush, rect);
            }

            var border = Rectangle.Inflate(rect, -1, -1);
            using (var pen = new Pen(Color.FromArgb(29, 78, 216)))
            {
                e.Graphics.DrawRectangle(pen, border);
            }

            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var textBrush = new SolidBrush(textColor))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Near;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                var measured = e.Graphics.MeasureString(Text, Font);
                var x = rect.X + ((rect.Width - measured.Width) / 2f);
                var y = rect.Y + ((rect.Height - measured.Height) / 2f) - 4f;
                var textRect = new RectangleF(x, y, measured.Width + 2f, measured.Height + 2f);
                e.Graphics.DrawString(Text, Font, textBrush, textRect, format);
            }

            if (Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -4, -4));
            }
        }
    }

    internal static class AppFonts
    {
        private static readonly PrivateFontCollection PrivateFonts = new PrivateFontCollection();
        private static readonly InstalledFontCollection InstalledFonts = new InstalledFontCollection();
        private static readonly FontFamily PreferredFamily = ResolvePreferredFamily();

        public static Font Regular(float size)
        {
            return Create(size, FontStyle.Regular);
        }

        public static Font Bold(float size)
        {
            return Create(size, FontStyle.Bold);
        }

        private static Font Create(float size, FontStyle style)
        {
            try
            {
                var safeStyle = PreferredFamily.IsStyleAvailable(style) ? style : FontStyle.Regular;
                return new Font(PreferredFamily, size, safeStyle, GraphicsUnit.Point);
            }
            catch
            {
                try
                {
                    return new Font("Noto Sans CJK KR", size, style, GraphicsUnit.Point);
                }
                catch
                {
                    return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
                }
            }
        }

        private static FontFamily ResolvePreferredFamily()
        {
            LoadBundledFonts();

            var bundled = FindFamily(PrivateFonts.Families);
            if (bundled != null)
            {
                return bundled;
            }

            var installed = FindFamily(InstalledFonts.Families);
            if (installed != null)
            {
                return installed;
            }

            return FontFamily.GenericSansSerif;
        }

        private static void LoadBundledFonts()
        {
            foreach (var dir in GetFontDirectories())
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(dir, "NotoSans*.?tf").Concat(Directory.GetFiles(dir, "NotoSans*.ttc")))
                {
                    try
                    {
                        PrivateFonts.AddFontFile(file);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static IEnumerable<string> GetFontDirectories()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
            yield return Path.Combine(Environment.CurrentDirectory, "Fonts");
        }

        private static FontFamily FindFamily(IEnumerable<FontFamily> families)
        {
            var names = new[] { "Noto Sans CJK KR", "Noto Sans KR", "Noto Sans" };
            foreach (var name in names)
            {
                var match = families.FirstOrDefault(family => family.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return families.FirstOrDefault(family => family.Name.IndexOf("Noto Sans", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal sealed class MainForm : Form
    {
        private const string AppTitle = "SMB Manager V1.7.9";
        private const string DefaultServerHost = "192.168.1.65";
        private const string CurrentVersionText = "V1.7.9";
        private const int AdminPasswordIterations = 120000;
        private const string UpdateApiUrl = "https://api.github.com/repos/droidbin/smb-manager/releases";
        private const string UpdateRepositoryUrl = "https://github.com/droidbin/smb-manager/releases";
        private const string EmptyResultLogMessage = "아직 기록된 처리 결과가 없습니다.";
        private const string AdminDepartmentLabel = "관리자";
        private const string ShortcutName = "SMB Manager.lnk";
        private const string EmbeddedSetupResourceName = "SmbManager.Bootstrap.Setup.exe";
        private const string EmbeddedUninstallResourceName = "SmbManager.Bootstrap.Uninstall.exe";
        private const int StartupReconnectDelayMilliseconds = 20000;

        private readonly string _configDir = ResolveWritableConfigDir();
        private readonly string _settingsPath;
        private readonly bool _startedFromWindowsStartup;
        private string _serverHost = DefaultServerHost;
        private string _lastDepartmentLabel = string.Empty;
        private bool _adminAuthenticated;
        private bool _automaticUpdate = true;
        private bool _includeBetaUpdates;
        private bool _connectionMonitorEnabled = true;
        private int _connectionMonitorIntervalSeconds = 60;
        private string _lastMonitorSummary = string.Empty;
        private string _adminPasswordSalt = string.Empty;
        private string _adminPasswordHash = string.Empty;
        private int _adminPasswordIterations = AdminPasswordIterations;
        private bool _settingsMigrated;
        private readonly List<FolderMapping> _folders = new List<FolderMapping>
        {
            new FolderMapping("지점공용", "Y:"),
            new FolderMapping("사업부", "X:"),
            new FolderMapping("바리스타학과", "W:"),
            new FolderMapping("제과제빵학과", "U:"),
            new FolderMapping("조리학과", "V:"),
            new FolderMapping("운영부", "Z:")
        };

        private readonly List<Department> _departments = new List<Department>
        {
            new Department("사업부", new byte[] { 35, 36, 49, 54, 54, 97 }, "지점공용", "사업부"),
            new Department("조리학과", new byte[] { 35, 36, 49, 54, 54, 98, 15, 51, 63, 63, 59 }, "지점공용", "조리학과"),
            new Department("제과제빵학과", new byte[] { 35, 36, 49, 54, 54, 98, 15, 50, 34, 53, 49, 52 }, "지점공용", "제과제빵학과"),
            new Department("바리스타학과", new byte[] { 35, 36, 49, 54, 54, 98, 15, 50, 49, 34, 57 }, "지점공용", "바리스타학과"),
            new Department("운영부", new byte[] { 35, 36, 49, 54, 54, 99 }, "지점공용", "운영부"),
            new Department("관리자", new byte[] { 35, 36, 49, 54, 54, 99, 15, 61 }, "지점공용", "사업부", "바리스타학과", "제과제빵학과", "조리학과", "운영부")
        };

        private ListBox _departmentList;
        private TextBox _passwordBox;
        private CheckBox _savePasswordBox;
        private TextBox _statusBox;
        private ListView _connectionView;
        private Button _connectButton;
        private Button _disconnectButton;
        private Button _repairButton;
        private Label _monitorStatusLabel;
        private CheckBox _monitorToggle;
        private System.Windows.Forms.Timer _connectionMonitorTimer;
        private TableLayoutPanel _rootLayout;
        private SplitContainer _mainSplit;
        private NotifyIcon _trayIcon;
        private bool _allowClose;
        private bool _trayHintShown;
        private bool _startupReconnectScheduled;
        private static readonly uint InstallerExitMessage = RegisterWindowMessage("SmbManager.Setup.RequestExit");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string messageName);

        public MainForm(bool startedFromWindowsStartup)
        {
            _startedFromWindowsStartup = startedFromWindowsStartup;
            Text = AppTitle;
            MinimumSize = new Size(860, 700);
            Size = new Size(980, 780);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(243, 243, 243);
            Font = AppFonts.Regular(9f);
            _settingsPath = Path.Combine(_configDir, "settings.ini");

            try
            {
                Environment.CurrentDirectory = Path.GetTempPath();
            }
            catch
            {
            }

            CleanupDuplicateExecutablesInCurrentDirectory();
            LoadSettings();
            CleanupPreviousInstalledVersions();
            BuildUi();
            RestoreLastDepartment();
            RefreshConnectionList();
            ConfigureConnectionMonitor();
            ConfigureTrayIcon();
            Shown += delegate
            {
                BeginInvoke(new Action(ApplyResponsiveLayout));
                if (NeedsEmbeddedInstallerBootstrap())
                {
                    LaunchEmbeddedInstaller();
                    return;
                }

                if (_startedFromWindowsStartup)
                {
                    HideToTray();
                    BeginStartupReconnect();
                }

                CheckForUpdatesOnStartup();
            };
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 1,
                RowCount = 4
            };
            _rootLayout = root;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.White, Padding = new Padding(14, 6, 10, 6) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            root.Controls.Add(header, 0, 0);

            header.Controls.Add(new Label
            {
                Text = "SMB Manager",
                Dock = DockStyle.Fill,
                Font = AppFonts.Bold(16f),
                ForeColor = Color.FromArgb(32, 32, 32),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            _monitorStatusLabel = new Label
            {
                Text = "모니터 준비",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(96, 96, 96),
                TextAlign = ContentAlignment.MiddleRight
            };
            header.Controls.Add(_monitorStatusLabel, 1, 0);

            var closeButton = new Button { Text = "종료", Dock = DockStyle.Fill };
            StyleButton(closeButton, false);
            closeButton.Click += delegate { ExitApplication(); };
            header.Controls.Add(closeButton, 2, 0);

            root.Controls.Add(BuildDepartmentPanel(), 0, 1);
            root.Controls.Add(BuildMainPanel(), 0, 2);

            _statusBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(17, 24, 39),
                ForeColor = Color.FromArgb(209, 213, 219),
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppFonts.Regular(9f)
            };
            root.Controls.Add(_statusBox, 0, 3);
            Log("준비됨");
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    HideToTray();
                    return;
                }

                ApplyResponsiveLayout();
            };
            FormClosing += MainForm_FormClosing;
        }

        private void ConfigureTrayIcon()
        {
            if (_trayIcon != null)
            {
                return;
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add("열기", null, delegate { RestoreFromTray(); });
            menu.Items.Add("종료", null, delegate { ExitApplication(); });

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "SMB Manager 실행 중",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_allowClose)
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                return;
            }

            e.Cancel = true;
            HideToTray();
        }

        protected override void WndProc(ref Message message)
        {
            if (InstallerExitMessage != 0 && message.Msg == (int)InstallerExitMessage)
            {
                BeginInvoke(new Action(ExitApplication));
                return;
            }

            base.WndProc(ref message);
        }

        private void HideToTray()
        {
            if (_trayIcon == null)
            {
                ConfigureTrayIcon();
            }

            Hide();
            ShowInTaskbar = false;
            if (!_trayHintShown && _trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(2500, "SMB Manager", "백그라운드에서 실행 중입니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
                _trayHintShown = true;
            }
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _allowClose = true;
            Close();
        }

        private void ApplyResponsiveLayout()
        {
            if (WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            if (_rootLayout != null)
            {
                var compact = ClientSize.Width < 900;
                _rootLayout.RowStyles[1].Height = compact ? 258 : 196;
                _rootLayout.RowStyles[3].Height = compact ? 144 : 122;
            }

            if (_mainSplit != null && _mainSplit.Width > 80 && _mainSplit.Height > 80)
            {
                if (ClientSize.Width < 900)
                {
                    _mainSplit.Orientation = Orientation.Horizontal;
                    SetSafeSplitterDistance(_mainSplit, Math.Max(210, (_mainSplit.Height * 58) / 100));
                }
                else
                {
                    _mainSplit.Orientation = Orientation.Vertical;
                    SetSafeSplitterDistance(_mainSplit, Math.Max(340, Math.Min(460, (_mainSplit.Width * 42) / 100)));
                }
            }
        }

        private static void SetSafeSplitterDistance(SplitContainer split, int desiredDistance)
        {
            if (split == null || split.Width <= 0 || split.Height <= 0)
            {
                return;
            }

            var span = split.Orientation == Orientation.Horizontal ? split.Height : split.Width;
            var minimum = split.Panel1MinSize;
            var maximum = span - split.SplitterWidth - split.Panel2MinSize;
            if (maximum < minimum)
            {
                return;
            }

            try
            {
                split.SplitterDistance = Math.Max(minimum, Math.Min(maximum, desiredDistance));
            }
            catch (InvalidOperationException)
            {
            }
        }

        private Control BuildDepartmentPanel()
        {
            var panel = new GroupBox { Text = "부서별 자동 연결", Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8) };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 4,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "부서", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

            _departmentList = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = AppFonts.Regular(10f),
                HorizontalScrollbar = true
            };
            layout.Controls.Add(_departmentList, 1, 0);
            layout.SetRowSpan(_departmentList, 3);

            RefreshDepartmentList(null);

            layout.Controls.Add(new Label
            {
                Text = "부서를 선택하고 비밀번호를 입력하세요.",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft
            }, 2, 0);
            layout.SetColumnSpan(layout.GetControlFromPosition(2, 0), 2);

            layout.Controls.Add(new Label { Text = "비밀번호", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 1);
            _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            layout.Controls.Add(_passwordBox, 3, 1);

            _connectButton = new CenteredTextButton
            {
                Text = "연결",
                Width = 96,
                Height = 36,
                BackColor = Color.FromArgb(0, 103, 192),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = AppFonts.Bold(9f),
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _connectButton.Click += delegate { BeginConnectSelectedDepartment(); };
            var connectButtonPanel = new Panel { Dock = DockStyle.Fill };
            connectButtonPanel.Controls.Add(_connectButton);
            connectButtonPanel.Resize += delegate { CenterConnectButton(connectButtonPanel); };
            layout.Controls.Add(connectButtonPanel, 2, 2);
            CenterConnectButton(connectButtonPanel);

            var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            var showPassword = new CheckBox { Text = "비밀번호 보이기", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
            showPassword.CheckedChanged += delegate { _passwordBox.UseSystemPasswordChar = !showPassword.Checked; };
            actionPanel.Controls.Add(showPassword);
            _savePasswordBox = new CheckBox { Text = "비밀번호 저장", AutoSize = true, Margin = new Padding(0, 7, 10, 0) };
            actionPanel.Controls.Add(_savePasswordBox);
            _disconnectButton = new Button { Text = "전체 연결 해제", Width = 110 };
            StyleButton(_disconnectButton, false);
            _disconnectButton.Click += delegate { DisconnectManagedFolders(true); RefreshConnectionList(); };
            actionPanel.Controls.Add(_disconnectButton);
            layout.Controls.Add(actionPanel, 3, 2);

            return panel;
        }

        private Control BuildMainPanel()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Panel1MinSize = 25, Panel2MinSize = 25, BackColor = Color.FromArgb(243, 243, 243) };
            _mainSplit = split;

            var left = new GroupBox { Text = "SMB 연결 상태", Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };
            var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            left.Controls.Add(leftLayout);

            _connectionView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            _connectionView.Columns.Add("드라이브", 70);
            _connectionView.Columns.Add("폴더", 130);
            _connectionView.Columns.Add("상태", 110);
            leftLayout.Controls.Add(_connectionView, 0, 0);
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            var refreshButton = new Button { Text = "새로고침", Width = 88 };
            StyleButton(refreshButton, false);
            refreshButton.Click += delegate { RefreshConnectionList(); };
            leftButtons.Controls.Add(refreshButton);
            _repairButton = new Button { Text = "진단/복구", Width = 92 };
            StyleButton(_repairButton, true);
            _repairButton.Click += delegate { BeginDiagnoseAndRepair(); };
            leftButtons.Controls.Add(_repairButton);
            var logButton = new Button { Text = "로그", Width = 70 };
            StyleButton(logButton, false);
            logButton.Click += delegate { OpenDiagnosticLog(); };
            leftButtons.Controls.Add(logButton);
            var updateButton = new Button { Text = "업데이트", Width = 82 };
            StyleButton(updateButton, false);
            updateButton.Click += delegate { CheckForUpdates(false); };
            leftButtons.Controls.Add(updateButton);
            var settingsButton = new Button { Text = "일반 설정", Width = 88 };
            StyleButton(settingsButton, false);
            settingsButton.Click += delegate { OpenGeneralSettings(); };
            leftButtons.Controls.Add(settingsButton);
            var securityButton = new Button { Text = "보안 설정", Width = 88 };
            StyleButton(securityButton, false);
            securityButton.Click += delegate { OpenSecuritySettings(); };
            leftButtons.Controls.Add(securityButton);
            _monitorToggle = new CheckBox { Text = "상태 모니터", AutoSize = true, Checked = _connectionMonitorEnabled, Margin = new Padding(8, 7, 8, 0) };
            _monitorToggle.CheckedChanged += delegate
            {
                _connectionMonitorEnabled = _monitorToggle.Checked;
                SaveSettings();
                ConfigureConnectionMonitor();
            };
            leftButtons.Controls.Add(_monitorToggle);
            leftLayout.Controls.Add(leftButtons, 0, 1);
            split.Panel1.Controls.Add(left);

            var right = new GroupBox { Text = "운영 패널", Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Color.White };
            right.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "설치 경로에서 실행 중인 앱이 시작 시 업데이트를 확인합니다.\r\n\r\n상태 모니터는 주기적으로 현재 매핑 상태만 읽고, 연결/해제나 자격 증명 변경은 수행하지 않습니다.\r\n\r\n연결이 꼬이면 진단/복구를 실행한 뒤 다시 연결하세요.",
                TextAlign = ContentAlignment.TopLeft
            });
            split.Panel2.Controls.Add(right);

            return split;
        }

        private void CenterConnectButton(Control container)
        {
            if (_connectButton == null || container == null)
            {
                return;
            }

            _connectButton.Left = Math.Max(0, (container.ClientSize.Width - _connectButton.Width) / 2);
            _connectButton.Top = Math.Max(0, (container.ClientSize.Height - _connectButton.Height) / 2);
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.Height = Math.Max(button.Height, 36);
            button.BackColor = primary ? Color.FromArgb(0, 103, 192) : Color.FromArgb(249, 249, 249);
            button.ForeColor = primary ? Color.White : Color.FromArgb(32, 32, 32);
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(0, 103, 192) : Color.FromArgb(204, 204, 204);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0, 91, 161) : Color.FromArgb(243, 243, 243);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0, 79, 140) : Color.FromArgb(230, 230, 230);
            button.Font = primary ? AppFonts.Bold(9f) : AppFonts.Regular(9f);
        }

        private void RefreshDepartmentList(string preferredLabel)
        {
            if (_departmentList == null)
            {
                return;
            }

            var current = preferredLabel;
            if (string.IsNullOrWhiteSpace(current))
            {
                var selected = _departmentList.SelectedItem as Department;
                current = selected == null ? string.Empty : selected.Label;
            }

            _departmentList.Items.Clear();
            foreach (var department in _departments)
            {
                _departmentList.Items.Add(department);
            }

            for (var i = 0; i < _departmentList.Items.Count; i++)
            {
                var department = _departmentList.Items[i] as Department;
                if (department != null && department.Label == current)
                {
                    _departmentList.SelectedIndex = i;
                    return;
                }
            }

            if (_departmentList.Items.Count > 0)
            {
                _departmentList.SelectedIndex = 0;
            }
        }

        private void BeginConnectSelectedDepartment()
        {
            var department = _departmentList.SelectedItem as Department;
            if (department == null)
            {
                MessageBox.Show("부서를 선택하세요.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var password = _passwordBox.Text;
            var usedSavedPassword = false;
            if (string.IsNullOrWhiteSpace(password))
            {
                if (!TryLoadSavedPassword(department.Label, out password))
                {
                    MessageBox.Show("비밀번호를 입력하세요.\r\n저장된 비밀번호가 있으면 입력 없이 연결할 수 있습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                usedSavedPassword = true;
            }

            var savePassword = _savePasswordBox != null && _savePasswordBox.Checked && !usedSavedPassword;
            SaveLastDepartment(department.Label);
            SetBusy(true);
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += delegate { ConnectSelectedDepartment(department, password, savePassword, usedSavedPassword); };
            worker.RunWorkerCompleted += delegate(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                if (e.Error != null)
                {
                    AppendResultLog("실패: 연결 처리 오류 / " + e.Error.Message);
                    MessageBox.Show("연결 처리 중 오류가 발생했습니다.\r\n" + e.Error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                RefreshConnectionList();
            };
            worker.RunWorkerAsync();
        }

        private void ConnectSelectedDepartment(Department department, string password, bool savePassword, bool usedSavedPassword)
        {
            var selected = _folders.Where(folder => department.AllowedShares.Contains(folder.Key)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("연결할 폴더를 찾지 못했습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DisconnectManagedFolders(true);
            if (!WaitForServerSessionRelease())
            {
                ClearPassword();
                AppendResultLog("실패: " + department.Label + " / 기존 서버 세션 정리 실패");
                MessageBox.Show(
                    "기존 서버 연결이 아직 남아 있어 새 로그인을 중단했습니다.\r\n\r\n열려 있는 공유폴더 창을 모두 닫고 전체 연결 해제를 누른 뒤 다시 시도해 주세요.",
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
            var authResult = ValidateCredential(department, password, selected[0]);
            if (authResult.ExitCode != 0)
            {
                ClearPassword();
                var failureReason = GetAuthenticationFailureReason(authResult.ExitCode);
                AppendResultLog("실패: " + department.Label + " / 인증 실패 / " + failureReason + " / 코드 " + authResult.ExitCode);
                MessageBox.Show(BuildAuthenticationFailureMessage("연결", authResult.ExitCode), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var connected = 0;
            var failed = new List<string>();
            var account = department.GetQualifiedAccount(_serverHost);
            if (savePassword)
            {
                SaveEncryptedPassword(department.Label, password);
                SaveCredentialForReconnect(department, password);
            }
            else if (usedSavedPassword)
            {
                SaveCredentialForReconnect(department, password);
            }
            else
            {
                DeleteSavedPassword(department.Label);
            }

            foreach (var folder in selected)
            {
                var unc = BuildUnc(folder.Share);
                var args = "use " + Quote(folder.Drive) + " " + Quote(unc) + " " + Quote(password) +
                           " /user:" + Quote(account) + " /persistent:" + (savePassword || usedSavedPassword ? "yes" : "no");
                var safeArgs = "use " + Quote(folder.Drive) + " " + Quote(unc) + " ****** /user:****** /persistent:" + (savePassword || usedSavedPassword ? "yes" : "no");
                var result = RunHidden("net", args, safeArgs, true, 30000);

                if (result.ExitCode == 0)
                {
                    connected++;
                    Log("연결됨: " + folder.Drive + " -> " + unc);
                }
                else
                {
                    failed.Add(folder.Share + "(" + result.ExitCode + ")");
                    Log("연결 실패: " + folder.Drive + " -> " + unc + " / 코드 " + result.ExitCode);
                }
            }

            ClearPassword();
            InvokeRefreshConnectionList();

            var verified = CountConnectedDrives(selected);
            var denied = FindAccessDeniedDrives(selected);
            if (verified > 0)
            {
                Process.Start("explorer.exe", "shell:MyComputerFolder");
            }

            var successMessage = department.Label + " 권한으로 업무폴더를 연결했습니다.\r\n연결 확인: " + verified + "개 / " + selected.Count + "개";
            if (denied.Count > 0)
            {
                successMessage += "\r\n\r\n다음 폴더는 연결은 되었지만 접근 권한이 없습니다.\r\n" +
                                  string.Join(", ", denied.ToArray()) +
                                  "\r\n\r\nNAS/서버에서 해당 계정의 공유 권한과 폴더 권한을 확인해 주세요.";
            }

            var resultSummary = failed.Count == 0
                ? "성공: " + department.Label + " / 연결 확인 " + verified + "개 / " + selected.Count + "개"
                : "실패: " + department.Label + " / " + string.Join(", ", failed.ToArray());
            if (denied.Count > 0)
            {
                resultSummary += " / 접근 거부: " + string.Join(", ", denied.ToArray());
            }
            AppendResultLog(resultSummary);

            MessageBox.Show(
                failed.Count == 0
                    ? successMessage
                    : "일부 폴더 연결에 실패했습니다.\r\n" + string.Join(", ", failed.ToArray()) + "\r\n\r\n전체 연결 해제를 누른 뒤 5초 후 다시 시도해 주세요.",
                AppTitle,
                MessageBoxButtons.OK,
                failed.Count == 0 && denied.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void ClearPassword()
        {
            if (_passwordBox.InvokeRequired)
            {
                _passwordBox.BeginInvoke(new Action(ClearPassword));
                return;
            }
            _passwordBox.Clear();
        }

        private void SetBusy(bool busy, string busyMessage = "연결 작업을 시작했습니다...")
        {
            _connectButton.Enabled = !busy;
            _disconnectButton.Enabled = !busy;
            if (_repairButton != null)
            {
                _repairButton.Enabled = !busy;
            }
            _departmentList.Enabled = !busy;
            if (busy)
            {
                Log(busyMessage);
            }
        }

        private void BeginDiagnoseAndRepair()
        {
            SetBusy(true, "SMB 진단 및 자동 복구를 시작했습니다...");
            var selectedDepartment = _departmentList == null ? null : _departmentList.SelectedItem as Department;
            var retryDepartmentLabel = !string.IsNullOrWhiteSpace(_lastDepartmentLabel)
                ? _lastDepartmentLabel
                : (selectedDepartment == null ? string.Empty : selectedDepartment.Label);
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += delegate(object sender, System.ComponentModel.DoWorkEventArgs e)
            {
                e.Result = DiagnoseAndRepairSmb(retryDepartmentLabel);
            };
            worker.RunWorkerCompleted += delegate(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                RefreshConnectionList();

                if (e.Error != null)
                {
                    AppendResultLog("실패: SMB 진단 및 자동 복구 / " + e.Error.Message);
                    MessageBox.Show("SMB 진단 및 자동 복구 중 오류가 발생했습니다.\r\n" + e.Error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var result = e.Result as RepairResult;
                if (result == null)
                {
                    MessageBox.Show("진단 결과를 확인할 수 없습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                MessageBox.Show(result.UserMessage, AppTitle, MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            worker.RunWorkerAsync();
        }

        private RepairResult DiagnoseAndRepairSmb(string retryDepartmentLabel)
        {
            var details = new List<string>();
            var warnings = new List<string>();
            var fixedItems = new List<string>();

            details.Add("서버: " + _serverHost);

            var serviceResult = EnsureWorkstationServiceRunning();
            details.Add(serviceResult);
            if (serviceResult.StartsWith("복구:", StringComparison.OrdinalIgnoreCase))
            {
                fixedItems.Add("Windows SMB 클라이언트 서비스 시작");
            }
            else if (serviceResult.StartsWith("주의:", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(serviceResult);
            }

            var pingResult = RunHidden("ping", "-n 1 -w 1200 " + Quote(_serverHost), null, false, 5000);
            if (pingResult.ExitCode == 0)
            {
                details.Add("확인: 서버 ping 응답");
            }
            else
            {
                warnings.Add("주의: 서버 ping 응답 없음");
                details.Add("주의: 서버 ping 응답 없음 / 코드 " + pingResult.ExitCode);
            }

            if (CanConnectToSmbPort(_serverHost, 1800))
            {
                details.Add("확인: SMB 포트 445 접속 가능");
            }
            else
            {
                warnings.Add("주의: SMB 포트 445 접속 실패");
                details.Add("주의: SMB 포트 445 접속 실패");
            }

            var driveWarnings = FindManagedDriveConflicts();
            foreach (var warning in driveWarnings)
            {
                warnings.Add(warning);
                details.Add(warning);
            }

            DisconnectManagedFolders(true);
            fixedItems.Add("관리 대상 드라이브와 서버 세션 정리");
            fixedItems.Add("저장된 서버 자격 증명 삭제");

            var released = WaitForServerSessionRelease();
            if (released)
            {
                details.Add("확인: 기존 서버 세션 정리 완료");
            }
            else
            {
                warnings.Add("주의: 열려 있는 공유폴더 창 때문에 서버 세션이 남아 있을 수 있음");
                details.Add("주의: 기존 서버 세션이 아직 감지됨");
            }

            Department retryDepartment = null;
            if (!string.IsNullOrWhiteSpace(retryDepartmentLabel))
            {
                retryDepartment = GetDepartmentByLabel(retryDepartmentLabel);
            }

            string savedPassword;
            if (released && retryDepartment != null && TryLoadSavedPassword(retryDepartment.Label, out savedPassword))
            {
                var reconnect = TryReconnectWithSavedPassword(retryDepartment, savedPassword);
                details.Add(reconnect.Message);
                if (reconnect.Success)
                {
                    fixedItems.Add("저장된 비밀번호로 " + retryDepartment.Label + " 자동 재연결");
                }
                else
                {
                    warnings.Add("주의: 저장된 비밀번호 자동 재연결 실패");
                }
            }
            else if (retryDepartment != null)
            {
                details.Add("확인: 저장된 비밀번호 없음 / 자동 재연결 생략");
            }

            var summary = warnings.Count == 0
                ? "성공: SMB 진단 및 자동 복구 / " + string.Join(", ", fixedItems.ToArray())
                : "주의: SMB 진단 및 자동 복구 / " + string.Join(", ", warnings.ToArray());
            AppendResultLog(summary + " / 상세: " + string.Join(" | ", details.ToArray()));

            var message = new StringBuilder();
            if (warnings.Count == 0)
            {
                message.AppendLine("SMB 연결 문제를 자동으로 점검하고 정리했습니다.");
                message.AppendLine();
                message.AppendLine(fixedItems.Any(item => item.Contains("자동 재연결"))
                    ? "저장된 비밀번호로 업무폴더 재연결까지 완료했습니다."
                    : "이제 부서를 선택해 다시 연결해 주세요.");
            }
            else
            {
                message.AppendLine("자동 복구를 실행했지만 확인이 필요한 항목이 있습니다.");
                message.AppendLine();
                foreach (var warning in warnings.Distinct().ToList())
                {
                    message.AppendLine("- " + warning);
                }
                message.AppendLine();
                message.AppendLine("공유폴더 창을 모두 닫은 뒤 다시 연결해 주세요.");
            }

            Log(warnings.Count == 0 ? "SMB 진단 및 자동 복구 완료" : "SMB 진단 및 자동 복구 완료: 확인 필요");
            return new RepairResult(warnings.Count == 0, message.ToString().Trim());
        }

        private string EnsureWorkstationServiceRunning()
        {
            var query = RunHidden("sc", "query LanmanWorkstation", null, false, 8000);
            if (query.ExitCode != 0)
            {
                return "주의: Windows SMB 클라이언트 서비스 상태 확인 실패";
            }

            if (query.Output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "확인: Windows SMB 클라이언트 서비스 실행 중";
            }

            var start = RunHidden("sc", "start LanmanWorkstation", null, false, 15000);
            Thread.Sleep(1000);
            var verify = RunHidden("sc", "query LanmanWorkstation", null, false, 8000);
            if (start.ExitCode == 0 || verify.Output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "복구: Windows SMB 클라이언트 서비스 시작";
            }

            return "주의: Windows SMB 클라이언트 서비스를 시작하지 못함";
        }

        private bool CanConnectToSmbPort(string host, int timeoutMilliseconds)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var async = client.BeginConnect(host, 445, null, null);
                    if (!async.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
                    {
                        return false;
                    }

                    client.EndConnect(async);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private List<string> FindManagedDriveConflicts()
        {
            var warnings = new List<string>();
            foreach (var folder in _folders)
            {
                try
                {
                    var root = NormalizeDrive(folder.Drive) + "\\";
                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    var info = new DriveInfo(root);
                    if (info.DriveType != DriveType.Network)
                    {
                        warnings.Add("주의: " + folder.Drive + " 드라이브가 네트워크 드라이브가 아니어서 자동 해제할 수 없음");
                    }
                }
                catch
                {
                }
            }

            return warnings;
        }

        private List<string> FindAccessDeniedDrives(List<FolderMapping> expected)
        {
            var denied = new List<string>();
            foreach (var folder in expected)
            {
                var result = RunHidden("cmd", "/c dir " + Quote(folder.Drive + "\\"), "dir " + Quote(folder.Drive + "\\"), false);
                if (result.ExitCode != 0)
                {
                    denied.Add(folder.Share);
                    Log("접근 권한 확인 실패: " + folder.Drive + " " + folder.Share + " / 코드 " + result.ExitCode);
                }
            }
            return denied;
        }

        private int CountConnectedDrives(List<FolderMapping> expected)
        {
            var result = RunHidden("net", "use", null);
            var count = 0;
            foreach (var folder in expected)
            {
                if (result.Output.Contains(folder.Drive))
                {
                    count++;
                }
            }
            return count;
        }

        private CommandResult ValidateCredential(Department department, string password, FolderMapping probeFolder)
        {
            var account = department.GetQualifiedAccount(_serverHost);
            var unc = BuildUnc(probeFolder.Share);
            var result = ValidateCredentialAgainstDrive(account, password, unc);
            if (result.ExitCode == 2 || result.ExitCode == 67)
            {
                result = ValidateCredentialAgainstUnc(account, password, "\\\\" + _serverHost + "\\IPC$");
            }

            return result;
        }

        private CommandResult ValidateAdminCredential(Department admin, string password)
        {
            var pathErrors = new List<CommandResult>();
            var selected = _folders.Where(folder => admin.AllowedShares.Contains(folder.Key)).ToList();
            foreach (var folder in selected)
            {
                var result = ValidateCredential(admin, password, folder);
                if (result.ExitCode == 0)
                {
                    return result;
                }

                if (result.ExitCode == 2 || result.ExitCode == 67)
                {
                    pathErrors.Add(result);
                    continue;
                }

                return result;
            }

            var account = admin.GetQualifiedAccount(_serverHost);
            var ipcResult = ValidateCredentialAgainstUnc(account, password, "\\\\" + _serverHost + "\\IPC$");
            if (ipcResult.ExitCode == 0 || (ipcResult.ExitCode != 2 && ipcResult.ExitCode != 67))
            {
                return ipcResult;
            }

            return pathErrors.Count > 0 ? pathErrors[0] : ipcResult;
        }

        private CommandResult ValidateCredentialAgainstDrive(string account, string password, string unc)
        {
            var probeDrive = GetProbeDrive();
            RunHidden("net", "use " + Quote(probeDrive) + " /delete /y", null, true, 15000);

            var args = "use " + Quote(probeDrive) + " " + Quote(unc) + " " + Quote(password) + " /user:" + Quote(account) + " /persistent:no";
            var result = RunHidden("net", args, "use " + Quote(probeDrive) + " " + Quote(unc) + " ****** /user:****** /persistent:no", true, 45000);

            RunHidden("net", "use " + Quote(probeDrive) + " /delete /y", null, true, 15000);
            WaitForServerSessionRelease();
            return result;
        }

        private string GetProbeDrive()
        {
            var used = new HashSet<string>(_folders.Select(folder => NormalizeDrive(folder.Drive)), StringComparer.OrdinalIgnoreCase);
            var candidates = new[] { "Q:", "R:", "S:", "T:", "P:", "O:" };
            foreach (var candidate in candidates)
            {
                if (!used.Contains(candidate) && !Directory.Exists(candidate + "\\"))
                {
                    return candidate;
                }
            }

            return "Q:";
        }

        private CommandResult ValidateCredentialAgainstUnc(string account, string password, string unc)
        {
            RunHidden("net", "use " + Quote(unc) + " /delete /y", null, true, 15000);

            var args = "use " + Quote(unc) + " " + Quote(password) + " /user:" + Quote(account) + " /persistent:no";
            var result = RunHidden("net", args, "use " + Quote(unc) + " ****** /user:****** /persistent:no", true, 45000);

            RunHidden("net", "use " + Quote(unc) + " /delete /y", null, true, 15000);
            WaitForServerSessionRelease();
            return result;
        }

        private static string GetAuthenticationFailureReason(int exitCode)
        {
            switch (exitCode)
            {
                case 2:
                    return "인증 확인 경로를 찾을 수 없음";
                case 5:
                    return "접근 권한 거부";
                case 53:
                    return "네트워크 경로를 찾을 수 없음";
                case 67:
                    return "공유 이름 또는 네트워크 이름을 찾을 수 없음";
                case 86:
                case 1326:
                    return "계정 또는 비밀번호 오류";
                case 1219:
                    return "기존 SMB 세션 충돌";
                case -999:
                    return "인증 명령 시간 초과";
                default:
                    return "인증 실패";
            }
        }

        private string BuildAuthenticationFailureMessage(string title, int exitCode)
        {
            var reason = GetAuthenticationFailureReason(exitCode);
            var message = title + "에 실패했습니다.\r\n\r\n원인: " + reason + "\r\n오류 코드: " + exitCode + "\r\n\r\n";

            switch (exitCode)
            {
                case 2:
                    if (title.IndexOf("관리자", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message += "일반 계정 연결이 정상이라면 PC 네트워크 문제가 아니라 관리자 SMB 계정의 공유 접근 권한 또는 계정 상태 문제일 가능성이 큽니다.\r\n서버/NAS에서 관리자 계정명, 비밀번호, 공유폴더 접근 권한을 확인해 주세요.";
                    }
                    else
                    {
                        message += "인증 확인에 사용한 공유 경로를 찾지 못했습니다.\r\n앱이 공유폴더에서 실행 중이거나, 서버 공유 이름/권한 상태가 PC마다 다르게 잡힌 경우 발생할 수 있습니다.\r\n앱을 로컬 폴더에 복사해 실행하거나, 서버에서 해당 계정의 공유폴더 접근 권한을 확인해 주세요.";
                    }
                    break;
                case 86:
                case 1326:
                    message += "입력한 비밀번호 또는 선택한 부서 계정이 맞는지 확인해 주세요.";
                    break;
                case 1219:
                    if (title.IndexOf("관리자", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message += "이 PC가 같은 서버에 이미 SMB 연결을 유지하고 있어 Windows가 다른 계정 인증 검증을 막았습니다.\r\n기존 공유폴더 연결은 해제하지 않았습니다.\r\n관리자 비밀번호를 저장해 둔 경우에는 저장된 값으로 연결 해제 없이 인증합니다.";
                    }
                    else
                    {
                        message += "이 PC가 같은 서버에 다른 계정으로 이미 연결되어 있습니다.\r\n공유폴더 창을 모두 닫고, 앱의 전체 연결 해제를 누른 뒤 다시 시도해 주세요.\r\n필요하면 Windows 자격 증명 관리자에서 192.168.1.65 관련 항목을 삭제해 주세요.";
                    }
                    break;
                case 53:
                    message += "서버에 접근할 수 없습니다.\r\n서버 IP, 네트워크 연결, 방화벽, VPN/유선망 상태를 확인해 주세요.\r\n현재 서버: " + _serverHost;
                    break;
                case 67:
                    message += "공유폴더 이름을 찾을 수 없습니다.\r\n서버의 공유 이름 또는 앱 설정의 서버 IP를 확인해 주세요.";
                    break;
                case 5:
                    message += "계정 인증은 되었지만 해당 공유폴더 접근 권한이 없을 수 있습니다.\r\nNAS/서버에서 계정의 공유 권한과 폴더 권한을 확인해 주세요.";
                    break;
                case -999:
                    message += "서버 응답이 지연되어 인증 확인 시간이 초과되었습니다.\r\n서버 IP, 네트워크 연결, 방화벽, SMB 접속 상태를 확인한 뒤 다시 시도해 주세요.\r\n처음 실행한 PC에서는 첫 인증 연결이 오래 걸릴 수 있으므로 잠시 후 다시 시도해 주세요.";
                    break;
                default:
                    message += "다른 PC에 남아 있는 SMB 세션이나 저장된 자격 증명이 원인일 수 있습니다.\r\n전체 연결 해제, 공유폴더 창 닫기, Windows 자격 증명 관리자 정리를 진행한 뒤 다시 시도해 주세요.";
                    break;
            }

            return message;
        }

        private ReconnectResult TryReconnectWithSavedPassword(Department department, string password)
        {
            var selected = _folders.Where(folder => department.AllowedShares.Contains(folder.Key)).ToList();
            if (selected.Count == 0)
            {
                return new ReconnectResult(false, "주의: 자동 재연결 대상 공유폴더 없음");
            }

            var authResult = ValidateCredential(department, password, selected[0]);
            if (authResult.ExitCode != 0)
            {
                return new ReconnectResult(false, "주의: 저장된 비밀번호 인증 실패 / " + GetAuthenticationFailureReason(authResult.ExitCode) + " / 코드 " + authResult.ExitCode);
            }

            var account = department.GetQualifiedAccount(_serverHost);
            SaveCredentialForReconnect(department, password);

            var connected = 0;
            var failed = new List<string>();
            foreach (var folder in selected)
            {
                var unc = BuildUnc(folder.Share);
                var args = "use " + Quote(folder.Drive) + " " + Quote(unc) + " " + Quote(password) +
                           " /user:" + Quote(account) + " /persistent:yes";
                var safeArgs = "use " + Quote(folder.Drive) + " " + Quote(unc) + " ****** /user:****** /persistent:yes";
                var result = RunHidden("net", args, safeArgs, true, 30000);

                if (result.ExitCode == 0)
                {
                    connected++;
                }
                else
                {
                    failed.Add(folder.Share + "(" + result.ExitCode + ")");
                }
            }

            InvokeRefreshConnectionList();

            if (failed.Count == 0)
            {
                return new ReconnectResult(true, "확인: 저장된 비밀번호 자동 재연결 완료 " + connected + "/" + selected.Count);
            }

            return new ReconnectResult(false, "주의: 저장된 비밀번호 자동 재연결 일부 실패 " + connected + "/" + selected.Count + " / " + string.Join(", ", failed.ToArray()));
        }

        private string GetSavedPasswordPath()
        {
            return Path.Combine(_configDir, "saved-passwords.ini");
        }

        private string GetSavedPasswordKey(string departmentLabel)
        {
            for (var i = 0; i < _departments.Count; i++)
            {
                if (_departments[i].Label == departmentLabel)
                {
                    return "Password" + i;
                }
            }

            return "Password_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(departmentLabel ?? string.Empty)).Replace("=", string.Empty);
        }

        private static byte[] GetPasswordEntropy()
        {
            return Encoding.UTF8.GetBytes("SMB Manager Saved Password V1");
        }

        private void SaveEncryptedPassword(string departmentLabel, string password)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var path = GetSavedPasswordPath();
                var values = File.Exists(path)
                    ? ReadSimpleIni(path)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var plain = Encoding.UTF8.GetBytes(password ?? string.Empty);
                var protectedBytes = ProtectedData.Protect(plain, GetPasswordEntropy(), DataProtectionScope.CurrentUser);
                values[GetSavedPasswordKey(departmentLabel)] = Convert.ToBase64String(protectedBytes);

                using (var writer = new StreamWriter(path, false, Encoding.UTF8))
                {
                    foreach (var item in values.OrderBy(item => item.Key))
                    {
                        writer.WriteLine(item.Key + "=" + item.Value);
                    }
                }

                Log("비밀번호를 Windows 사용자 암호화 저장소에 저장했습니다.");
            }
            catch (Exception error)
            {
                Log("비밀번호 저장 실패: " + error.Message);
                AppendResultLog("실패: 비밀번호 저장 / " + error.Message);
            }
        }

        private void DeleteSavedPassword(string departmentLabel)
        {
            try
            {
                var path = GetSavedPasswordPath();
                if (!File.Exists(path)) return;
                var values = ReadSimpleIni(path);
                if (!values.Remove(GetSavedPasswordKey(departmentLabel))) return;
                using (var writer = new StreamWriter(path, false, Encoding.UTF8))
                {
                    foreach (var item in values.OrderBy(item => item.Key))
                        writer.WriteLine(item.Key + "=" + item.Value);
                }
            }
            catch (Exception error)
            {
                Log("저장된 비밀번호 삭제 실패: " + error.Message);
            }
        }

        private bool TryLoadSavedPassword(string departmentLabel, out string password)
        {
            password = string.Empty;
            try
            {
                var path = GetSavedPasswordPath();
                if (!File.Exists(path))
                {
                    return false;
                }

                var values = ReadSimpleIni(path);
                string encoded;
                if (!values.TryGetValue(GetSavedPasswordKey(departmentLabel), out encoded) || string.IsNullOrWhiteSpace(encoded))
                {
                    return false;
                }

                var protectedBytes = Convert.FromBase64String(encoded);
                var plain = ProtectedData.Unprotect(protectedBytes, GetPasswordEntropy(), DataProtectionScope.CurrentUser);
                password = Encoding.UTF8.GetString(plain);
                return !string.IsNullOrEmpty(password);
            }
            catch (Exception error)
            {
                Log("저장된 비밀번호 읽기 실패: " + error.Message);
                AppendResultLog("실패: 저장된 비밀번호 읽기 / " + error.Message);
                return false;
            }
        }

        private void SaveCredentialForReconnect(Department department, string password)
        {
            DeleteStoredCredentials();

            var account = department.GetQualifiedAccount(_serverHost);
            var saved = WriteDomainCredential(_serverHost, account, password);
            var savedUncTarget = WriteDomainCredential("\\\\" + _serverHost, account, password);
            if (saved || savedUncTarget)
            {
                Log("재부팅 후 복원을 위한 Windows 자격 증명을 저장했습니다.");
            }
            else
            {
                var errorCode = Marshal.GetLastWin32Error();
                Log("Windows 자격 증명 저장 실패: Windows 오류 " + errorCode);
                AppendResultLog("실패: Windows 자격 증명 저장 / 오류 " + errorCode);
            }
        }

        private bool WriteDomainCredential(string targetName, string userName, string password)
        {
            var passwordPointer = IntPtr.Zero;
            try
            {
                passwordPointer = Marshal.StringToCoTaskMemUni(password);
                var credential = new NativeCredential
                {
                    Flags = 0,
                    Type = CredentialTypeDomainPassword,
                    TargetName = targetName,
                    Comment = "Saved by SMB Manager",
                    CredentialBlobSize = Encoding.Unicode.GetByteCount(password),
                    CredentialBlob = passwordPointer,
                    Persist = CredentialPersistLocalMachine,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = null,
                    UserName = userName
                };

                return CredWrite(ref credential, 0);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (passwordPointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeCoTaskMemUnicode(passwordPointer);
                }
            }
        }

        private void DeleteStoredCredentials()
        {
            CredDelete(_serverHost, CredentialTypeDomainPassword, 0);
            CredDelete("\\\\" + _serverHost, CredentialTypeDomainPassword, 0);
            RunHidden("cmdkey", "/delete:" + _serverHost, null);
            RunHidden("cmdkey", "/delete:" + Quote("\\\\" + _serverHost), null);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string targetName, int type, int flags);

        private const int CredentialTypeDomainPassword = 2;
        private const int CredentialPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public NativeFileTime LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public int LowDateTime;
            public int HighDateTime;
        }

        private void DisconnectManagedFolders(bool removeCredential)
        {
            Log("기존 업무폴더 연결을 정리하는 중...");
            foreach (var folder in _folders)
            {
                RunHidden("net", "use " + Quote(folder.Drive) + " /delete /y", null);
                RunHidden("net", "use " + Quote(BuildUnc(folder.Share)) + " /delete /y", null);
            }

            RunHidden("net", "use " + Quote("\\\\" + _serverHost + "\\IPC$") + " /delete /y", null);
            RunHidden("net", "use " + Quote("\\\\" + _serverHost) + " /delete /y", null);

            if (removeCredential)
            {
                DeleteStoredCredentials();
                Log("저장된 서버 자격 증명 정리 완료: " + _serverHost);
            }

            InvokeRefreshConnectionList();
        }

        private bool WaitForServerSessionRelease()
        {
            for (var i = 0; i < 3; i++)
            {
                Thread.Sleep(700);
                var result = RunHidden("net", "use", null);
                if (!result.Output.Contains("\\\\" + _serverHost + "\\"))
                {
                    return true;
                }
            }

            return false;
        }

        private void RefreshConnectionList()
        {
            if (_connectionView == null)
            {
                return;
            }

            _connectionView.Items.Clear();
            var result = RunHidden("net", "use", null);
            foreach (var folder in _folders)
            {
                var connected = result.Output.Contains(folder.Drive);
                var status = connected ? GetAccessStatus(folder) : "미연결";
                var item = new ListViewItem(folder.Drive);
                item.SubItems.Add(folder.Share);
                item.SubItems.Add(status);
                if (status == "접속중")
                {
                    item.BackColor = Color.FromArgb(232, 248, 239);
                }
                else if (status == "접근 거부")
                {
                    item.BackColor = Color.FromArgb(255, 242, 230);
                }
                _connectionView.Items.Add(item);
            }
        }

        private void ConfigureConnectionMonitor()
        {
            if (_connectionMonitorTimer == null)
            {
                _connectionMonitorTimer = new System.Windows.Forms.Timer();
                _connectionMonitorTimer.Tick += delegate { RunPassiveConnectionMonitor(); };
            }

            _connectionMonitorTimer.Stop();
            _connectionMonitorTimer.Interval = Math.Max(15, _connectionMonitorIntervalSeconds) * 1000;
            if (_connectionMonitorEnabled)
            {
                _connectionMonitorTimer.Start();
                SetMonitorStatus("모니터 ON");
            }
            else
            {
                SetMonitorStatus("모니터 OFF");
            }

            if (_monitorToggle != null && _monitorToggle.Checked != _connectionMonitorEnabled)
            {
                _monitorToggle.Checked = _connectionMonitorEnabled;
            }
        }

        private void BeginStartupReconnect()
        {
            if (_startupReconnectScheduled)
            {
                return;
            }

            _startupReconnectScheduled = true;
            Log("Windows 시작 감지: 잠시 후 저장된 비밀번호로 업무폴더를 재연결합니다.");
            AppendResultLog("확인: Windows 시작 자동 재연결 대기 / " + (StartupReconnectDelayMilliseconds / 1000) + "초");
            SetMonitorStatus("자동 재연결 대기");

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = StartupReconnectDelayMilliseconds;
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                RunStartupReconnect();
            };
            timer.Start();
        }

        private void RunStartupReconnect()
        {
            Log("Windows 시작 자동 재연결을 실행합니다.");
            SetMonitorStatus("자동 재연결 중");

            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += delegate(object sender, System.ComponentModel.DoWorkEventArgs e)
            {
                e.Result = ReconnectLastDepartmentFromStartup();
            };
            worker.RunWorkerCompleted += delegate(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
            {
                if (e.Error != null)
                {
                    AppendResultLog("실패: Windows 시작 자동 재연결 / " + e.Error.Message);
                    Log("Windows 시작 자동 재연결 실패: " + e.Error.Message);
                    SetMonitorStatus("자동 재연결 실패");
                    return;
                }

                var result = e.Result as ReconnectResult;
                if (result == null)
                {
                    Log("Windows 시작 자동 재연결 결과를 확인하지 못했습니다.");
                    SetMonitorStatus("자동 재연결 확인 필요");
                    return;
                }

                AppendResultLog((result.Success ? "성공: " : "확인: ") + "Windows 시작 자동 재연결 / " + result.Message);
                Log("Windows 시작 자동 재연결: " + result.Message);
                SetMonitorStatus(result.Success ? "자동 재연결 완료" : "자동 재연결 생략");
                RefreshConnectionList();
            };
            worker.RunWorkerAsync();
        }

        private ReconnectResult ReconnectLastDepartmentFromStartup()
        {
            if (string.IsNullOrWhiteSpace(_lastDepartmentLabel))
            {
                return new ReconnectResult(false, "마지막 연결 부서 없음");
            }

            var department = GetDepartmentByLabel(_lastDepartmentLabel);
            if (department == null)
            {
                return new ReconnectResult(false, "마지막 연결 부서를 찾을 수 없음: " + _lastDepartmentLabel);
            }

            string savedPassword;
            if (!TryLoadSavedPassword(department.Label, out savedPassword))
            {
                return new ReconnectResult(false, department.Label + " 저장된 비밀번호 없음");
            }

            DisconnectManagedFolders(true);
            if (!WaitForServerSessionRelease())
            {
                return new ReconnectResult(false, "기존 서버 세션 정리 실패");
            }

            return TryReconnectWithSavedPassword(department, savedPassword);
        }

        private void RunPassiveConnectionMonitor()
        {
            if (!_connectionMonitorEnabled)
            {
                return;
            }

            try
            {
                var result = RunHidden("net", "use", null, false);
                var connected = 0;
                foreach (var folder in _folders)
                {
                    if (result.Output.Contains(folder.Drive))
                    {
                        connected++;
                    }
                }

                SetMonitorStatus("모니터 ON / " + connected + "/" + _folders.Count);
                var summary = connected + "/" + _folders.Count;
                if (summary == _lastMonitorSummary)
                {
                    return;
                }

                _lastMonitorSummary = summary;
                if (connected == 0)
                {
                    Log("상태 모니터: 연결된 업무폴더가 없습니다.");
                }
                else if (connected < _folders.Count)
                {
                    Log("상태 모니터: 일부 업무폴더만 연결됨 " + connected + "/" + _folders.Count);
                }
            }
            catch (Exception error)
            {
                SetMonitorStatus("모니터 확인 실패");
                AppendResultLog("실패: 상태 모니터 / " + error.Message);
            }
        }

        private void SetMonitorStatus(string message)
        {
            if (_monitorStatusLabel == null)
            {
                return;
            }

            _monitorStatusLabel.Text = message + " · " + CurrentVersionText;
        }

        private string GetAccessStatus(FolderMapping folder)
        {
            var result = RunHidden("cmd", "/c dir " + Quote(folder.Drive + "\\"), "dir " + Quote(folder.Drive + "\\"), false);
            return result.ExitCode == 0 ? "접속중" : "접근 거부";
        }

        private void InvokeRefreshConnectionList()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshConnectionList));
                return;
            }
            RefreshConnectionList();
        }

        private CommandResult RunHidden(string fileName, string arguments, string safeArguments, bool logOutput = true, int timeoutMilliseconds = 12000)
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
            };

            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    return new CommandResult(-999, "Command timed out after " + (timeoutMilliseconds / 1000) + " seconds.");
                }

                Task.WaitAll(stdout, stderr);
                var output = stdout.Result + stderr.Result;
                return new CommandResult(process.ExitCode, output);
            }
        }

        private void AppendResultLog(string message)
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var logPath = GetLogPath();
                var append = true;
                if (File.Exists(logPath))
                {
                    var current = File.ReadAllText(logPath, Encoding.UTF8).Trim();
                    append = current.Length > 0 && current != EmptyResultLogMessage;
                }

                using (var writer = new StreamWriter(logPath, append, Encoding.UTF8))
                {
                    writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + message);
                }
            }
            catch
            {
            }
        }

        private void Log(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), message);
                return;
            }

            if (_statusBox == null)
            {
                return;
            }

            _statusBox.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine + _statusBox.Text;
        }

        private string BuildUnc(string share)
        {
            return "\\\\" + _serverHost + "\\" + share;
        }

        private static Dictionary<string, string> ReadSimpleIni(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                values[line.Substring(0, index).Trim()] = line.Substring(index + 1).Trim();
            }

            return values;
        }

        private static int CompareVersionText(string left, string right)
        {
            var leftParts = ParseVersionParts(left);
            var rightParts = ParseVersionParts(right);
            var length = Math.Max(leftParts.Length, rightParts.Length);

            for (var i = 0; i < length; i++)
            {
                var a = i < leftParts.Length ? leftParts[i] : 0;
                var b = i < rightParts.Length ? rightParts[i] : 0;
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            var leftSuffix = (left ?? string.Empty).Split(new[] { '-' }, 2);
            var rightSuffix = (right ?? string.Empty).Split(new[] { '-' }, 2);
            if (leftSuffix.Length == 1 || rightSuffix.Length == 1)
            {
                return leftSuffix.Length == rightSuffix.Length ? 0 : (leftSuffix.Length == 1 ? 1 : -1);
            }
            var leftIds = leftSuffix[1].Split('.');
            var rightIds = rightSuffix[1].Split('.');
            for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
            {
                int a, b;
                var aNumber = int.TryParse(leftIds[i], out a);
                var bNumber = int.TryParse(rightIds[i], out b);
                var comparison = aNumber && bNumber ? a.CompareTo(b)
                    : aNumber != bNumber ? (aNumber ? -1 : 1)
                    : string.Compare(leftIds[i], rightIds[i], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
            return leftIds.Length.CompareTo(rightIds.Length);
        }

        private static int[] ParseVersionParts(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(1);
            }

            var dash = value.IndexOf('-');
            if (dash >= 0)
            {
                value = value.Substring(0, dash);
            }

            return value
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                {
                    int number;
                    return int.TryParse(part, out number) ? number : 0;
                })
                .ToArray();
        }

        private string GetLogPath()
        {
            return Path.Combine(_configDir, "result.log");
        }

        private void OpenDiagnosticLog()
        {
            try
            {
                Directory.CreateDirectory(_configDir);
                var logPath = GetLogPath();
                if (!File.Exists(logPath))
                {
                    File.WriteAllText(logPath, EmptyResultLogMessage, Encoding.UTF8);
                }
                Process.Start("notepad.exe", logPath);
            }
            catch (Exception error)
            {
                MessageBox.Show("진단 로그를 열 수 없습니다.\r\n" + error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CheckForUpdatesOnStartup()
        {
            CheckForUpdates(true);
        }

        private static bool NeedsEmbeddedInstallerBootstrap()
        {
            return NeedsEmbeddedInstallerBootstrap(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static bool NeedsEmbeddedInstallerBootstrap(string appDirectory)
        {
            try
            {
                var currentDir = NormalizeDirectoryPath(appDirectory);
                var uninstallPath = Path.Combine(currentDir, "Uninstall.exe");
                if (!EmbeddedFileMatches(EmbeddedUninstallResourceName, uninstallPath))
                {
                    return true;
                }

                return !HasValidInstallInfo(currentDir);
            }
            catch
            {
                return true;
            }
        }

        private static bool HasValidInstallInfo(string appDirectory)
        {
            var infoPath = Path.Combine(appDirectory, "install-info.ini");
            if (!File.Exists(infoPath))
            {
                return false;
            }

            string product = null;
            string installDir = null;
            foreach (var line in File.ReadAllLines(infoPath, Encoding.UTF8))
            {
                if (line.StartsWith("Product=", StringComparison.OrdinalIgnoreCase))
                {
                    product = line.Substring("Product=".Length).Trim();
                }
                else if (line.StartsWith("InstallDir=", StringComparison.OrdinalIgnoreCase))
                {
                    installDir = line.Substring("InstallDir=".Length).Trim();
                }
            }

            return string.Equals(product, "SMB Manager", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(installDir)
                && string.Equals(NormalizeDirectoryPath(installDir), NormalizeDirectoryPath(appDirectory), StringComparison.OrdinalIgnoreCase);
        }

        private static bool EmbeddedFileMatches(string resourceName, string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            using (var resource = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName))
            using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (resource == null || resource.Length != file.Length)
                {
                    return false;
                }

                using (var hash = SHA256.Create())
                {
                    var resourceHash = hash.ComputeHash(resource);
                    var fileHash = hash.ComputeHash(file);
                    return SlowEquals(Convert.ToBase64String(resourceHash), Convert.ToBase64String(fileHash));
                }
            }
        }

        private void LaunchEmbeddedInstaller()
        {
            try
            {
                var bootstrapRoot = Path.Combine(Path.GetTempPath(), "SMB Manager Bootstrap", CurrentVersionText + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bootstrapRoot);

                var sourceApp = Path.Combine(bootstrapRoot, "SMB Manager " + CurrentVersionText + ".exe");
                File.Copy(Application.ExecutablePath, sourceApp, true);

                var setupPath = Path.Combine(bootstrapRoot, "Setup.exe");
                ExtractEmbeddedFile(EmbeddedSetupResourceName, setupPath);
                ExtractEmbeddedFile(EmbeddedUninstallResourceName, Path.Combine(bootstrapRoot, "Uninstall.exe"));
                CopyBootstrapCompanionIfExists("README.md", bootstrapRoot);
                CopyBootstrapFontsIfExists(bootstrapRoot);

                var versionPath = Path.Combine(bootstrapRoot, "version.ini");
                File.WriteAllText(versionPath, "Version=" + CurrentVersionText + "\r\nOwner=Codex\r\n", Encoding.UTF8);

                AppendResultLog("성공: 구버전 EXE 업데이트 / 정식 설치 전환");
                Log("구버전 업데이트 설치 상태를 감지해 설치 프로그램을 실행합니다.");
                MessageBox.Show(
                    "구버전 업데이트 방식으로 실행 파일만 교체된 상태입니다.\r\n\r\n정식 설치 프로그램을 실행합니다. 설치 버튼을 눌러 완료해 주세요.",
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    WorkingDirectory = bootstrapRoot,
                    UseShellExecute = true
                });
                ExitApplication();
            }
            catch (Exception error)
            {
                AppendResultLog("실패: 정식 설치 전환 / " + error.Message);
                MessageBox.Show(
                    "정식 설치 프로그램을 준비하지 못했습니다.\r\n" + error.Message,
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void ExtractEmbeddedFile(string resourceName, string destinationPath)
        {
            using (var source = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName))
            {
                if (source == null)
                {
                    throw new FileNotFoundException("내장 설치 파일을 찾을 수 없습니다: " + resourceName);
                }

                using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }
            }
        }

        private static void CopyBootstrapCompanionIfExists(string fileName, string targetDir)
        {
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(targetDir, fileName), true);
            }
        }

        private static void CopyBootstrapFontsIfExists(string targetDir)
        {
            var sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
            if (!Directory.Exists(sourceDir))
            {
                return;
            }

            CopyDirectory(sourceDir, Path.Combine(targetDir, "Fonts"));
        }

        private void CheckForUpdates(bool automatic)
        {
            try
            {
                var release = GetLatestGitHubRelease(_includeBetaUpdates);
                if (release == null || string.IsNullOrWhiteSpace(release.Version))
                {
                    if (automatic)
                    {
                        return;
                    }

                    MessageBox.Show("선택한 채널에 설치 가능한 배포 버전이 없습니다.\r\n업데이트 채널: " + (_includeBetaUpdates ? "베타 버전 포함" : "정식 버전만") + "\r\n\r\n" + UpdateRepositoryUrl, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var comparison = CompareVersionText(release.Version, CurrentVersionText);
                if (comparison <= 0)
                {
                    if (automatic)
                    {
                        return;
                    }

                    MessageBox.Show("선택한 채널에 현재 버전보다 새로운 업데이트가 없습니다.\r\n현재 버전: " + CurrentVersionText + "\r\n업데이트 채널: " + (_includeBetaUpdates ? "베타 버전 포함" : "정식 버전만") + "\r\n\r\n낮은 버전으로 자동 변경하지 않습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    AppendResultLog("성공: 업데이트 확인 / 최신 버전");
                    return;
                }

                if (string.IsNullOrWhiteSpace(release.ZipDownloadUrl))
                {
                    if (!automatic)
                    {
                        MessageBox.Show("최신 릴리즈에서 설치 zip 파일을 찾을 수 없습니다.\r\n\r\n릴리즈에 \"SMB Manager " + release.Version + ".zip\" 파일을 업로드해 주세요.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                var prompt = "새 버전이 있습니다.\r\n\r\n현재 버전: " + CurrentVersionText + "\r\n최신 버전: " + release.Version;
                prompt += "\r\n배포 위치: GitHub Releases";
                prompt += "\r\n업데이트 채널: " + (_includeBetaUpdates ? "베타 버전 포함" : "정식 버전만");
                if (!string.IsNullOrWhiteSpace(release.Message))
                {
                    prompt += "\r\n\r\n" + release.Message;
                }
                prompt += "\r\n\r\n지금 다운로드하고 설치할까요?";

                AppendResultLog("성공: 업데이트 확인 / 새 버전 " + release.Version);
                if (automatic)
                {
                    if (_automaticUpdate)
                    {
                        Log("GitHub에서 새 버전을 자동 설치합니다: " + release.Version);
                        DownloadAndInstallUpdate(release, true);
                    }
                    else
                    {
                        Log("새 버전이 있습니다: " + release.Version);
                    }

                    return;
                }

                if (MessageBox.Show(prompt, AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    DownloadAndInstallUpdate(release, false);
                }
            }
            catch (UpdateNotPublishedException error)
            {
                AppendResultLog("성공: 업데이트 확인 / 릴리즈 미배포");
                if (!automatic)
                {
                    MessageBox.Show(error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception error)
            {
                AppendResultLog("실패: 업데이트 확인 / " + error.Message);
                if (!automatic)
                {
                    MessageBox.Show("업데이트 확인 중 오류가 발생했습니다.\r\n" + error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static GitHubReleaseInfo GetLatestGitHubRelease(bool includeBeta)
        {
            EnsureTls12();
            using (var client = CreateWebClient())
            {
                GitHubReleaseInfo latest = null;
                for (var page = 1; ; page++)
                {
                string json;
                try
                {
                    json = client.DownloadString(UpdateApiUrl + "?per_page=100&page=" + page);
                }
                catch (WebException error)
                {
                    var response = error.Response as HttpWebResponse;
                    if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new UpdateNotPublishedException(
                            "아직 GitHub Releases에 배포된 버전이 없습니다.\r\n\r\n" +
                            "현재 버전: " + CurrentVersionText + "\r\n" +
                            "릴리즈 페이지: " + UpdateRepositoryUrl + "\r\n\r\n" +
                            "업데이트 서버로 사용하려면 GitHub에서 새 Release를 만들고 배포 zip 파일을 asset으로 업로드해 주세요.");
                    }

                    throw;
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var releases = serializer.Deserialize<GitHubReleasePayload[]>(json) ?? new GitHubReleasePayload[0];
                var candidate = SelectLatestRelease(releases, includeBeta);
                if (candidate != null && (latest == null || CompareVersionText(candidate.Version, latest.Version) > 0))
                    latest = candidate;
                if (releases.Length < 100) return latest;
                }
            }
        }

        private static GitHubReleaseInfo SelectLatestRelease(GitHubReleasePayload[] releases, bool includeBeta)
        {
            GitHubReleaseInfo latest = null;
            foreach (var item in releases)
            {
                if (item == null || item.draft || string.IsNullOrWhiteSpace(item.tag_name)) continue;
                var tag = item.tag_name.Trim();
                // The retired 2.x release line must never be offered by this 1.x client.
                if (!Regex.IsMatch(tag, @"^[vV]?1\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?$", RegexOptions.IgnoreCase)) continue;
                if (!includeBeta && (item.prerelease || tag.IndexOf('-') >= 0)) continue;
                var version = "V" + tag.TrimStart('v', 'V');
                var expectedName = "SMB Manager " + version + ".zip";
                var asset = (item.assets ?? new GitHubAssetPayload[0]).FirstOrDefault(a => a != null
                    && string.Equals(a.name, expectedName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(a.browser_download_url));
                if (asset == null) continue;
                if (latest == null || CompareVersionText(version, latest.Version) > 0)
                    latest = new GitHubReleaseInfo { Version = version, Message = TrimReleaseMessage(item.body),
                        ZipFileName = asset.name, ZipDownloadUrl = asset.browser_download_url };
            }
            return latest;
        }

        private static string GetFileNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractJsonString(string json, string propertyName)
        {
            var pattern = "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"";
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value : string.Empty;
        }

        private static string JsonDecode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return Regex.Unescape(value.Replace("\\/", "/"));
        }

        private static string TrimReleaseMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            message = message.Trim();
            return message.Length <= 600 ? message : message.Substring(0, 600) + "...";
        }

        private static WebClient CreateWebClient()
        {
            var client = new WebClient();
            client.Encoding = Encoding.UTF8;
            client.Headers[HttpRequestHeader.UserAgent] = "SMB Manager Update Client";
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static void EnsureTls12()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch
            {
            }
        }

        private void DownloadAndInstallUpdate(GitHubReleaseInfo release, bool silent)
        {
            var installRoot = GetLocalUpdateInstallRoot();
            var versionDir = Path.Combine(installRoot, MakeSafeFileName(release.Version));
            var downloadRoot = Path.Combine(installRoot, "_downloads");
            var extractDir = Path.Combine(downloadRoot, MakeSafeFileName(release.Version));
            Directory.CreateDirectory(downloadRoot);

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            Directory.CreateDirectory(extractDir);

            var zipPath = Path.Combine(downloadRoot, string.IsNullOrWhiteSpace(release.ZipFileName) ? "SMBManager-" + release.Version + ".zip" : MakeSafeFileName(release.ZipFileName));
            EnsureTls12();
            using (var client = CreateWebClient())
            {
                client.DownloadFile(release.ZipDownloadUrl, zipPath);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var setupPath = Directory.GetFiles(extractDir, "Setup.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
            {
                throw new FileNotFoundException("다운로드한 업데이트 zip 안에서 Setup.exe를 찾을 수 없습니다.");
            }

            Directory.CreateDirectory(versionDir);
            File.WriteAllText(Path.Combine(installRoot, "pending-setup.txt"), setupPath, Encoding.UTF8);

            AppendResultLog("성공: 업데이트 다운로드 / " + release.Version);
            if (!silent)
            {
                MessageBox.Show("업데이트 파일을 다운로드했습니다.\r\n설치 프로그램을 실행합니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = setupPath,
                WorkingDirectory = Path.GetDirectoryName(setupPath),
                UseShellExecute = true
            });

            ExitApplication();
        }

        private static void CreateDesktopShortcut(string targetPath, string workingDirectory)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop))
                {
                    return;
                }

                var shortcutPath = Path.Combine(desktop, ShortcutName);
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return;
                }

                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "SMB Manager" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
            }
        }

        private static void CopyCurrentCompanionFiles(string targetDir)
        {
            CopyCurrentFileIfExists("README.md", targetDir);
            CopyCurrentFileIfExists("Uninstall.exe", targetDir);

            var sourceFonts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
            var targetFonts = Path.Combine(targetDir, "Fonts");
            if (Directory.Exists(sourceFonts))
            {
                if (Directory.Exists(targetFonts))
                {
                    Directory.Delete(targetFonts, true);
                }

                CopyDirectory(sourceFonts, targetFonts);
            }
        }

        private static void CopyCurrentFileIfExists(string fileName, string targetDir)
        {
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(targetDir, fileName), true);
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(directory, Path.Combine(targetDir, Path.GetFileName(directory)));
            }
        }

        private static void CleanupPreviousInstalledVersions()
        {
            CleanupPreviousInstalledVersions(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static void CleanupDuplicateExecutablesInCurrentDirectory()
        {
            try
            {
                var currentExe = Path.GetFullPath(Application.ExecutablePath);
                var currentDir = Path.GetDirectoryName(currentExe);
                if (string.IsNullOrWhiteSpace(currentDir) || !Directory.Exists(currentDir))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(currentDir, "SMB Manager V*.exe"))
                {
                    if (Path.GetFullPath(file).Equals(currentExe, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDeleteFile(file);
                }
            }
            catch
            {
            }
        }

        private static void WriteVersionFile(string targetDir, string version)
        {
            try
            {
                var text = "Version=" + version + "\r\nOwner=Codex\r\nPolicy=Codex manages release version updates from this file.\r\n";
                File.WriteAllText(Path.Combine(targetDir, "version.ini"), text, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void CleanupPreviousInstalledVersions(string keepDir)
        {
            try
            {
                var installRoot = GetLocalUpdateInstallRoot();
                if (!Directory.Exists(installRoot))
                {
                    return;
                }

                var keep = NormalizeDirectoryPath(keepDir);
                foreach (var directory in Directory.GetDirectories(installRoot))
                {
                    if (NormalizeDirectoryPath(directory).Equals(keep, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    TryDeleteDirectory(directory);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
        private static string GetLocalUpdateInstallRoot()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.GetTempPath();
            }

            return Path.Combine(baseDir, "SMB Manager", "Installed");
        }

        private static string MakeSafeFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "Update" : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }

        private void OpenGeneralSettings()
        {
            using (var form = new AdminSettingsForm(_serverHost, _folders, _departments, _automaticUpdate, _connectionMonitorEnabled, _connectionMonitorIntervalSeconds, false, _includeBetaUpdates))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _serverHost = form.ServerHost;
                _automaticUpdate = form.AutomaticUpdate;
                _includeBetaUpdates = form.IncludeBetaUpdates;
                _connectionMonitorEnabled = form.ConnectionMonitorEnabled;
                _connectionMonitorIntervalSeconds = form.ConnectionMonitorIntervalSeconds;
                for (var i = 0; i < _folders.Count; i++)
                {
                    _folders[i].Drive = form.Drives[i];
                    _folders[i].Share = form.Shares[i];
                }

                SaveSettings();
                RefreshConnectionList();
                ConfigureConnectionMonitor();
                Log("일반 설정을 저장했습니다.");
            }
        }

        private void OpenSecuritySettings()
        {
            if (!EnsureAdminAuthenticated())
            {
                return;
            }

            try
            {
                using (var form = new AdminSettingsForm(_serverHost, _folders, _departments, _automaticUpdate, _connectionMonitorEnabled, _connectionMonitorIntervalSeconds, true))
                {
                    if (form.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    var accounts = form.Accounts;
                    for (var i = 0; i < _departments.Count && i < accounts.Count; i++)
                    {
                        _departments[i].SetAccount(accounts[i]);
                    }

                    if (!string.IsNullOrWhiteSpace(form.NewAdminPassword))
                    {
                        SetAdminPassword(form.NewAdminPassword);
                    }

                    SaveSettings();
                    Log("보안 설정을 저장했습니다.");
                }
            }
            finally
            {
                _adminAuthenticated = false;
                RefreshDepartmentList(_lastDepartmentLabel);
            }
        }

        private bool EnsureAdminAuthenticated()
        {
            try
            {
                if (_adminAuthenticated)
                {
                    return true;
                }

                if (!HasAdminPassword())
                {
                    using (var setup = new AdminPasswordSetupForm("관리자 비밀번호 설정", "보안 설정을 보호할 앱 내부 비밀번호를 설정하세요."))
                    {
                        if (setup.ShowDialog(this) != DialogResult.OK)
                        {
                            return false;
                        }

                        SetAdminPassword(setup.Password);
                        SaveSettings();
                        AppendResultLog("성공: 내부 관리자 비밀번호 최초 설정");
                        _adminAuthenticated = true;
                        Log("보안 설정 비밀번호를 최초 설정했습니다.");
                        return true;
                    }
                }

                using (var form = new AdminAuthForm())
                {
                    if (form.ShowDialog(this) != DialogResult.OK)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(form.Password))
                    {
                        MessageBox.Show("관리자 비밀번호를 입력하세요.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }

                    if (!VerifyAdminPassword(form.Password))
                    {
                        AppendResultLog("실패: 내부 관리자 인증 / 비밀번호 불일치");
                        MessageBox.Show("관리자 비밀번호가 맞지 않습니다.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }

                _adminAuthenticated = true;
                RefreshDepartmentList(AdminDepartmentLabel);
                AppendResultLog("성공: 내부 관리자 인증");
                Log("관리자 인증이 완료되었습니다.");
                return true;
            }
            catch (Exception error)
            {
                AppendResultLog("실패: 관리자 인증 예외 / " + error.Message);
                MessageBox.Show("관리자 인증 중 오류가 발생했습니다.\r\n" + error.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private Department GetAdminDepartment()
        {
            return _departments.FirstOrDefault(IsAdminDepartment);
        }

        private static bool IsAdminDepartment(Department department)
        {
            return department != null && department.Label == AdminDepartmentLabel;
        }

        private Department GetDepartmentByLabel(string label)
        {
            return _departments.FirstOrDefault(department => department.Label == label);
        }

        private void LoadSettings()
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            foreach (var line in File.ReadAllLines(_settingsPath, Encoding.UTF8))
            {
                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, index);
                var value = line.Substring(index + 1);
                if (key == "ServerHost" && !string.IsNullOrWhiteSpace(value))
                {
                    _serverHost = value.Trim();
                }
                else if (key == "LastDepartment")
                {
                    _lastDepartmentLabel = value.Trim();
                }
                else if (key == "AutomaticUpdate")
                {
                    _automaticUpdate = IsTruthy(value, true);
                }
                else if (key == "ConnectionMonitorEnabled")
                {
                    _connectionMonitorEnabled = IsTruthy(value, true);
                }
                else if (key == "UpdateChannel")
                {
                    _includeBetaUpdates = string.Equals(value.Trim(), "Beta", StringComparison.OrdinalIgnoreCase);
                }
                else if (key == "ConnectionMonitorIntervalSeconds")
                {
                    int seconds;
                    if (int.TryParse(value, out seconds))
                    {
                        _connectionMonitorIntervalSeconds = Math.Max(15, Math.Min(3600, seconds));
                    }
                }
                else if (key == "AdminPasswordSalt")
                {
                    _adminPasswordSalt = value.Trim();
                }
                else if (key == "AdminPasswordHash")
                {
                    _adminPasswordHash = value.Trim();
                }
                else if (key == "AdminPasswordIterations")
                {
                    int iterations;
                    if (int.TryParse(value, out iterations) && iterations > 0)
                    {
                        _adminPasswordIterations = iterations;
                    }
                }
                else if (key.StartsWith("Drive", StringComparison.OrdinalIgnoreCase))
                {
                    int folderIndex;
                    if (int.TryParse(key.Substring(5), out folderIndex) && folderIndex >= 0 && folderIndex < _folders.Count)
                    {
                        _folders[folderIndex].Drive = NormalizeDrive(value);
                    }
                }
                else if (key.StartsWith("Share", StringComparison.OrdinalIgnoreCase))
                {
                    int folderIndex;
                    if (int.TryParse(key.Substring(5), out folderIndex) && folderIndex >= 0 && folderIndex < _folders.Count && !string.IsNullOrWhiteSpace(value))
                    {
                        _folders[folderIndex].Share = value.Trim();
                    }
                }
                else if (key.StartsWith("AccountProtected", StringComparison.OrdinalIgnoreCase))
                {
                    int departmentIndex;
                    if (int.TryParse(key.Substring(16), out departmentIndex) && departmentIndex >= 0 && departmentIndex < _departments.Count)
                    {
                        var account = DecodeProtectedSettingValue(value);
                        if (!string.IsNullOrWhiteSpace(account))
                        {
                            _departments[departmentIndex].SetAccount(account);
                        }
                    }
                }
                else if (key.StartsWith("Account", StringComparison.OrdinalIgnoreCase))
                {
                    int departmentIndex;
                    if (int.TryParse(key.Substring(7), out departmentIndex) && departmentIndex >= 0 && departmentIndex < _departments.Count)
                    {
                        var account = DecodeSettingValue(value);
                        if (!string.IsNullOrWhiteSpace(account))
                        {
                            _departments[departmentIndex].SetAccount(account);
                            _settingsMigrated = true;
                        }
                    }
                }
            }

            if (_settingsMigrated)
            {
                SaveSettings();
            }
        }

        private void SaveSettings()
        {
            Directory.CreateDirectory(_configDir);
            using (var writer = new StreamWriter(_settingsPath, false, Encoding.UTF8))
            {
                writer.WriteLine("ServerHost=" + _serverHost);
                writer.WriteLine("LastDepartment=" + _lastDepartmentLabel);
                writer.WriteLine("AutomaticUpdate=" + _automaticUpdate);
                writer.WriteLine("UpdateChannel=" + (_includeBetaUpdates ? "Beta" : "Stable"));
                writer.WriteLine("ConnectionMonitorEnabled=" + _connectionMonitorEnabled);
                writer.WriteLine("ConnectionMonitorIntervalSeconds=" + _connectionMonitorIntervalSeconds);
                writer.WriteLine("AdminPasswordSalt=" + _adminPasswordSalt);
                writer.WriteLine("AdminPasswordHash=" + _adminPasswordHash);
                writer.WriteLine("AdminPasswordIterations=" + _adminPasswordIterations);
                for (var i = 0; i < _folders.Count; i++)
                {
                    writer.WriteLine("Share" + i + "=" + _folders[i].Share);
                    writer.WriteLine("Drive" + i + "=" + _folders[i].Drive);
                }
                for (var i = 0; i < _departments.Count; i++)
                {
                    writer.WriteLine("AccountProtected" + i + "=" + EncodeProtectedSettingValue(_departments[i].GetAccount()));
                }
            }
        }

        private bool HasAdminPassword()
        {
            return !string.IsNullOrWhiteSpace(_adminPasswordSalt) && !string.IsNullOrWhiteSpace(_adminPasswordHash);
        }

        private void SetAdminPassword(string password)
        {
            _adminPasswordSalt = CreateRandomBase64(16);
            _adminPasswordIterations = AdminPasswordIterations;
            _adminPasswordHash = HashAdminPassword(password, _adminPasswordSalt);
        }

        private bool VerifyAdminPassword(string password)
        {
            if (!HasAdminPassword())
            {
                return false;
            }

            var candidate = HashAdminPassword(password, _adminPasswordSalt);
            if (SlowEquals(candidate, _adminPasswordHash))
            {
                return true;
            }

            var legacy = HashAdminPasswordLegacy(password, _adminPasswordSalt);
            if (SlowEquals(legacy, _adminPasswordHash))
            {
                SetAdminPassword(password);
                SaveSettings();
                return true;
            }

            return false;
        }

        private static string CreateRandomBase64(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes);
        }

        private static string HashAdminPassword(string password, string saltBase64)
        {
            var salt = Convert.FromBase64String(saltBase64);
            using (var derive = new Rfc2898DeriveBytes(password ?? string.Empty, salt, AdminPasswordIterations))
            {
                return Convert.ToBase64String(derive.GetBytes(32));
            }
        }

        private static string HashAdminPasswordLegacy(string password, string saltBase64)
        {
            var salt = Convert.FromBase64String(saltBase64);
            var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            var input = new byte[salt.Length + passwordBytes.Length];
            Buffer.BlockCopy(salt, 0, input, 0, salt.Length);
            Buffer.BlockCopy(passwordBytes, 0, input, salt.Length, passwordBytes.Length);

            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(input));
            }
        }

        private static bool SlowEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            var diff = leftBytes.Length ^ rightBytes.Length;
            var length = Math.Min(leftBytes.Length, rightBytes.Length);
            for (var i = 0; i < length; i++)
            {
                diff |= leftBytes[i] ^ rightBytes[i];
            }

            return diff == 0;
        }

        private static string EncodeSettingValue(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string EncodeProtectedSettingValue(string value)
        {
            var plain = Encoding.UTF8.GetBytes(value ?? string.Empty);
            var protectedBytes = ProtectedData.Protect(plain, GetSettingEntropy(), DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static string DecodeProtectedSettingValue(string value)
        {
            try
            {
                var protectedBytes = Convert.FromBase64String(value ?? string.Empty);
                var plain = ProtectedData.Unprotect(protectedBytes, GetSettingEntropy(), DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] GetSettingEntropy()
        {
            return Encoding.UTF8.GetBytes("SMB Manager Settings V2");
        }

        private static string DecodeSettingValue(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsTruthy(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            value = value.Trim();
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return defaultValue;
        }

        private void SaveLastDepartment(string label)
        {
            _lastDepartmentLabel = label;
            SaveSettings();
        }

        private void RestoreLastDepartment()
        {
            if (string.IsNullOrWhiteSpace(_lastDepartmentLabel))
            {
                return;
            }

            for (var i = 0; i < _departmentList.Items.Count; i++)
            {
                var department = _departmentList.Items[i] as Department;
                if (department != null && department.Label == _lastDepartmentLabel)
                {
                    _departmentList.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string NormalizeDrive(string value)
        {
            value = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length == 1 && value[0] >= 'A' && value[0] <= 'Z')
            {
                value += ":";
            }
            return value;
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string ResolveWritableConfigDir()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMB Manager"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMB Manager"),
                Path.Combine(Path.GetTempPath(), "SMB Manager")
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    var testPath = Path.Combine(candidate, ".write-test");
                    File.WriteAllText(testPath, "ok", Encoding.UTF8);
                    File.Delete(testPath);
                    return candidate;
                }
                catch
                {
                }
            }

            return Path.GetTempPath();
        }
    }

    internal sealed class FolderMapping
    {
        public FolderMapping(string share, string drive)
        {
            Key = share;
            Share = share;
            Drive = drive;
        }

        public string Key { get; private set; }
        public string Share { get; set; }
        public string Drive { get; set; }
    }

    public sealed class GitHubReleasePayload
    {
        public string tag_name { get; set; }
        public string body { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public GitHubAssetPayload[] assets { get; set; }
    }

    public sealed class GitHubAssetPayload
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
    }

    internal sealed class GitHubReleaseInfo
    {
        public string Version { get; set; }
        public string Message { get; set; }
        public string ZipFileName { get; set; }
        public string ZipDownloadUrl { get; set; }
    }

    internal sealed class UpdateNotPublishedException : Exception
    {
        public UpdateNotPublishedException(string message)
            : base(message)
        {
        }
    }

    internal sealed class Department
    {
        private const byte Mask = 0x50;
        private readonly byte[] _encodedAccount;
        private string _accountOverride;

        public Department(string label, byte[] encodedAccount, params string[] allowedShares)
        {
            Label = label;
            _encodedAccount = encodedAccount;
            AllowedShares = allowedShares.ToList();
        }

        public string Label { get; private set; }
        public List<string> AllowedShares { get; private set; }

        public string GetAccount()
        {
            if (!string.IsNullOrWhiteSpace(_accountOverride))
            {
                return _accountOverride;
            }

            var chars = new char[_encodedAccount.Length];
            for (var i = 0; i < _encodedAccount.Length; i++)
            {
                chars[i] = (char)(_encodedAccount[i] ^ Mask);
            }

            return new string(chars);
        }

        public void SetAccount(string account)
        {
            if (!string.IsNullOrWhiteSpace(account))
            {
                _accountOverride = account.Trim();
            }
        }

        public string GetQualifiedAccount(string host)
        {
            return host + "\\" + GetAccount();
        }

        public override string ToString()
        {
            return Label;
        }
    }

    internal sealed class AdminAuthForm : Form
    {
        private readonly TextBox _passwordBox;

        public AdminAuthForm()
        {
            Text = "관리자 인증";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(360, 170);
            Font = AppFonts.Regular(9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 3
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var hint = new Label
            {
                Text = "관리자 기능 사용을 위해 인증이 필요합니다.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(hint, 0, 0);
            root.SetColumnSpan(hint, 2);

            root.Controls.Add(new Label { Text = "비밀번호", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            root.Controls.Add(_passwordBox, 1, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "인증", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 80 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 2);
            root.SetColumnSpan(buttons, 2);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string Password
        {
            get { return _passwordBox.Text; }
        }
    }

    internal sealed class AdminPasswordSetupForm : Form
    {
        private readonly TextBox _passwordBox;
        private readonly TextBox _confirmBox;

        public AdminPasswordSetupForm(string title, string message)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(420, 220);
            Font = AppFonts.Regular(9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 2,
                RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(root);

            var hint = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(hint, 0, 0);
            root.SetColumnSpan(hint, 2);

            root.Controls.Add(new Label { Text = "새 비밀번호", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            _passwordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            root.Controls.Add(_passwordBox, 1, 1);

            root.Controls.Add(new Label { Text = "비밀번호 확인", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            _confirmBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            root.Controls.Add(_confirmBox, 1, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Width = 80 };
            ok.Click += delegate
            {
                if (!ValidatePassword())
                {
                    DialogResult = DialogResult.None;
                }
            };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 80 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 3);
            root.SetColumnSpan(buttons, 2);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        public string Password
        {
            get { return _passwordBox.Text; }
        }

        private bool ValidatePassword()
        {
            if (string.IsNullOrWhiteSpace(_passwordBox.Text) || _passwordBox.Text.Length < 4)
            {
                MessageBox.Show("관리자 비밀번호는 4자 이상 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (_passwordBox.Text != _confirmBox.Text)
            {
                MessageBox.Show("비밀번호 확인이 일치하지 않습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            return true;
        }
    }

    internal sealed class AdminSettingsForm : Form
    {
        private TextBox _serverBox;
        private CheckBox _automaticUpdateBox;
        private ComboBox _updateChannelBox;
        private CheckBox _monitorEnabledBox;
        private NumericUpDown _monitorIntervalBox;
        private TextBox _newAdminPasswordBox;
        private TextBox _confirmAdminPasswordBox;
        private readonly List<TextBox> _shareBoxes = new List<TextBox>();
        private readonly List<TextBox> _driveBoxes = new List<TextBox>();
        private readonly List<TextBox> _accountBoxes = new List<TextBox>();
        private readonly List<FolderMapping> _folders;
        private readonly List<Department> _departments;
        private readonly bool _securityMode;

        public AdminSettingsForm(string serverHost, List<FolderMapping> folders, List<Department> departments, bool automaticUpdate, bool monitorEnabled, int monitorIntervalSeconds, bool securityMode, bool includeBetaUpdates = false)
        {
            _folders = folders;
            _departments = departments;
            _securityMode = securityMode;
            Text = securityMode ? "보안 설정" : "일반 설정";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(560, 520);
            Font = AppFonts.Regular(9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                ColumnCount = 1,
                RowCount = 2
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            if (securityMode)
            {
                tabs.TabPages.Add(BuildAccountTab(departments));
                tabs.TabPages.Add(BuildSecurityTab());
            }
            else
            {
                tabs.TabPages.Add(BuildGeneralTab(serverHost, automaticUpdate, monitorEnabled, monitorIntervalSeconds, includeBetaUpdates));
                tabs.TabPages.Add(BuildFolderTab(folders));
            }
            root.Controls.Add(tabs, 0, 0);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Width = 80 };
            ok.Click += delegate
            {
                if (!ValidateSettings())
                {
                    DialogResult = DialogResult.None;
                }
            };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 80 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 1);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private TabPage BuildGeneralTab(string serverHost, bool automaticUpdate, bool monitorEnabled, int monitorIntervalSeconds, bool includeBetaUpdates)
        {
            var page = new TabPage("일반");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "서버 IP/호스트", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _serverBox = new TextBox { Dock = DockStyle.Fill, Text = serverHost };
            layout.Controls.Add(_serverBox, 1, 0);

            _automaticUpdateBox = new CheckBox { Text = "실행 시 GitHub 릴리즈 자동 업데이트", AutoSize = true, Checked = automaticUpdate };
            layout.Controls.Add(new Label { Text = "업데이트", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            layout.Controls.Add(_automaticUpdateBox, 1, 1);

            _monitorEnabledBox = new CheckBox { Text = "SMB 상태 모니터 사용", AutoSize = true, Checked = monitorEnabled };
            layout.Controls.Add(new Label { Text = "상태 모니터", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            layout.Controls.Add(_monitorEnabledBox, 1, 2);

            _monitorIntervalBox = new NumericUpDown { Dock = DockStyle.Left, Width = 80, Minimum = 15, Maximum = 3600, Increment = 15, Value = Math.Max(15, Math.Min(3600, monitorIntervalSeconds)) };
            layout.Controls.Add(new Label { Text = "점검 주기(초)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
            layout.Controls.Add(_monitorIntervalBox, 1, 3);

            _updateChannelBox = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _updateChannelBox.Items.AddRange(new object[] { "정식 버전만", "베타 버전 포함" });
            _updateChannelBox.SelectedIndex = includeBetaUpdates ? 1 : 0;
            layout.Controls.Add(new Label { Text = "업데이트 채널", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
            layout.Controls.Add(_updateChannelBox, 1, 4);

            layout.Controls.Add(new Label
            {
                Text = "베타 버전 포함: 정식 버전과 베타 중 새로운 버전을 확인합니다.\r\n정식 버전만: 정식 업데이트만 확인하며, 낮은 버전으로 변경하지 않습니다.",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft
            }, 0, 5);
            layout.SetColumnSpan(layout.GetControlFromPosition(0, 5), 2);
            return page;
        }

        private TabPage BuildFolderTab(List<FolderMapping> folders)
        {
            var page = new TabPage("공유폴더");
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(12), ColumnCount = 3, RowCount = folders.Count + 1, AutoSize = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "항목", Dock = DockStyle.Fill, Font = AppFonts.Bold(9f) }, 0, 0);
            layout.Controls.Add(new Label { Text = "공유폴더 이름", Dock = DockStyle.Fill, Font = AppFonts.Bold(9f) }, 1, 0);
            layout.Controls.Add(new Label { Text = "드라이브", Dock = DockStyle.Fill, Font = AppFonts.Bold(9f) }, 2, 0);
            for (var i = 0; i < folders.Count; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.Controls.Add(new Label { Text = folders[i].Key, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i + 1);
                var shareBox = new TextBox { Dock = DockStyle.Fill, Text = folders[i].Share };
                var driveBox = new TextBox { Dock = DockStyle.Left, Width = 55, Text = folders[i].Drive };
                _shareBoxes.Add(shareBox);
                _driveBoxes.Add(driveBox);
                layout.Controls.Add(shareBox, 1, i + 1);
                layout.Controls.Add(driveBox, 2, i + 1);
            }
            return page;
        }

        private TabPage BuildAccountTab(List<Department> departments)
        {
            var page = new TabPage("계정");
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(12), ColumnCount = 2, RowCount = departments.Count + 2, AutoSize = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "부서", Dock = DockStyle.Fill, Font = AppFonts.Bold(9f) }, 0, 0);
            layout.Controls.Add(new Label { Text = "SMB 계정명", Dock = DockStyle.Fill, Font = AppFonts.Bold(9f) }, 1, 0);
            for (var i = 0; i < departments.Count; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                layout.Controls.Add(new Label { Text = departments[i].Label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i + 1);
                var box = new TextBox { Dock = DockStyle.Fill, Text = departments[i].GetAccount() };
                _accountBoxes.Add(box);
                layout.Controls.Add(box, 1, i + 1);
            }

            var hint = new Label
            {
                Text = "이 탭은 SMB 사용자 계정명만 수정합니다. 비밀번호 저장은 연결 화면에서 선택합니다.",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft
            };
            layout.Controls.Add(hint, 0, departments.Count + 1);
            layout.SetColumnSpan(hint, 2);
            return page;
        }

        private TabPage BuildSecurityTab()
        {
            var page = new TabPage("보안");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "관리자 비밀번호", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _newAdminPasswordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            layout.Controls.Add(_newAdminPasswordBox, 1, 0);

            layout.Controls.Add(new Label { Text = "비밀번호 확인", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            _confirmAdminPasswordBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            layout.Controls.Add(_confirmAdminPasswordBox, 1, 1);

            var hint = new Label
            {
                Text = "입력한 경우에만 앱 내부 관리자 비밀번호가 변경됩니다. SMB 서버 계정 비밀번호와는 별개입니다.",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.TopLeft
            };
            layout.Controls.Add(hint, 0, 2);
            layout.SetColumnSpan(hint, 2);
            return page;
        }

        public string ServerHost
        {
            get { return _serverBox == null ? string.Empty : _serverBox.Text.Trim(); }
        }

        public List<string> Drives
        {
            get { return _driveBoxes.Select(box => NormalizeDrive(box.Text)).ToList(); }
        }

        public List<string> Shares
        {
            get { return _shareBoxes.Select(box => box.Text.Trim()).ToList(); }
        }

        public List<string> Accounts
        {
            get { return _accountBoxes.Select(box => box.Text.Trim()).ToList(); }
        }

        public string NewAdminPassword
        {
            get { return _newAdminPasswordBox == null ? string.Empty : _newAdminPasswordBox.Text; }
        }

        public bool AutomaticUpdate
        {
            get { return _automaticUpdateBox != null && _automaticUpdateBox.Checked; }
        }

        public bool IncludeBetaUpdates
        {
            get { return _updateChannelBox != null && _updateChannelBox.SelectedIndex == 1; }
        }

        public bool ConnectionMonitorEnabled
        {
            get { return _monitorEnabledBox != null && _monitorEnabledBox.Checked; }
        }

        public int ConnectionMonitorIntervalSeconds
        {
            get { return _monitorIntervalBox == null ? 60 : (int)_monitorIntervalBox.Value; }
        }

        private bool ValidateSettings()
        {
            if (!_securityMode && string.IsNullOrWhiteSpace(ServerHost))
            {
                MessageBox.Show("서버 IP를 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            foreach (var drive in Drives)
            {
                if (drive.Length != 2 || drive[1] != ':' || drive[0] < 'A' || drive[0] > 'Z')
                {
                    MessageBox.Show("드라이브 문자는 Z: 형식으로 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            foreach (var share in Shares)
            {
                if (string.IsNullOrWhiteSpace(share) || share.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || share.Contains("\\") || share.Contains("/"))
                {
                    MessageBox.Show("공유폴더 이름을 올바르게 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            foreach (var account in Accounts)
            {
                if (string.IsNullOrWhiteSpace(account))
                {
                    MessageBox.Show("계정명을 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(NewAdminPassword))
            {
                if (NewAdminPassword.Length < 4)
                {
                    MessageBox.Show("관리자 비밀번호는 4자 이상 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (_confirmAdminPasswordBox == null || NewAdminPassword != _confirmAdminPasswordBox.Text)
                {
                    MessageBox.Show("관리자 비밀번호 확인이 일치하지 않습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            return true;
        }

        private static string NormalizeDrive(string value)
        {
            value = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length == 1 && value[0] >= 'A' && value[0] <= 'Z')
            {
                value += ":";
            }
            return value;
        }
    }

    internal sealed class CommandResult
    {
        public CommandResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output ?? string.Empty;
        }

        public int ExitCode { get; private set; }
        public string Output { get; private set; }
    }

    internal sealed class RepairResult
    {
        public RepairResult(bool success, string userMessage)
        {
            Success = success;
            UserMessage = userMessage ?? string.Empty;
        }

        public bool Success { get; private set; }
        public string UserMessage { get; private set; }
    }

    internal sealed class ReconnectResult
    {
        public ReconnectResult(bool success, string message)
        {
            Success = success;
            Message = message ?? string.Empty;
        }

        public bool Success { get; private set; }
        public string Message { get; private set; }
    }
}
