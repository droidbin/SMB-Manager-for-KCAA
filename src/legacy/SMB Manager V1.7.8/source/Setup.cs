using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SmbManagerSetup
{
    internal static class SetupProgram
    {
        private const string ProductName = "SMB Manager";
        private const string AppFolderName = "SMB Manager";
        private const string ShortcutName = "SMB Manager.lnk";
        private const string UninstallShortcutName = "SMB Manager Uninstall.lnk";
        private const string StartupArgument = "--startup";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }

        private static class SetupFonts
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
                    return new Font(PreferredFamily, size, style, GraphicsUnit.Point);
                }
                catch
                {
                    return new Font("Noto Sans CJK KR", size, style, GraphicsUnit.Point);
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
                var fontDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
                if (!Directory.Exists(fontDir))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(fontDir, "NotoSans*.?tf").Concat(Directory.GetFiles(fontDir, "NotoSans*.ttc")))
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

            private static FontFamily FindFamily(FontFamily[] families)
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

        private sealed class SetupForm : Form
        {
            private const int GracefulExitWaitMilliseconds = 4000;
            private const int ForcedExitWaitMilliseconds = 5000;
            private static readonly uint InstallerExitMessage = RegisterWindowMessage("SmbManager.Setup.RequestExit");
            private readonly TextBox _installPathBox;
            private readonly CheckBox _desktopShortcutBox;
            private readonly CheckBox _startMenuShortcutBox;
            private readonly CheckBox _startupShortcutBox;
            private readonly CheckBox _launchAfterInstallBox;
            private readonly TextBox _logBox;
            private readonly Button _installButton;

            private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern uint RegisterWindowMessage(string messageName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

            public SetupForm()
            {
                Text = ProductName + " Setup";
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(560, 430);
                Size = new Size(640, 480);
                Font = SetupFonts.Regular(9f);
                BackColor = Color.FromArgb(243, 246, 250);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    ColumnCount = 1,
                    RowCount = 5
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                Controls.Add(root);

                root.Controls.Add(new Label
                {
                    Text = ProductName + " Installer",
                    Dock = DockStyle.Fill,
                    Font = SetupFonts.Bold(16f),
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, 0);

                var pathPanel = new GroupBox { Text = "Install location", Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };
                var pathLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
                pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                pathLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                _installPathBox = new TextBox { Dock = DockStyle.Fill, Text = GetDefaultInstallPath() };
                var browseButton = new Button { Text = "Browse", Dock = DockStyle.Fill };
                browseButton.Click += delegate { BrowseInstallPath(); };
                pathLayout.Controls.Add(_installPathBox, 0, 0);
                pathLayout.Controls.Add(browseButton, 1, 0);
                pathPanel.Controls.Add(pathLayout);
                root.Controls.Add(pathPanel, 0, 1);

                var optionPanel = new GroupBox { Text = "Options", Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };
                var optionLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                _desktopShortcutBox = new CheckBox { Text = "Create desktop shortcut", AutoSize = true, Checked = true };
                _startMenuShortcutBox = new CheckBox { Text = "Create Start Menu shortcut", AutoSize = true, Checked = true };
                _startupShortcutBox = new CheckBox { Text = "Start with Windows and reconnect saved folders", AutoSize = true, Checked = true };
                _launchAfterInstallBox = new CheckBox { Text = "Launch after install", AutoSize = true, Checked = true };
                optionLayout.Controls.Add(_desktopShortcutBox);
                optionLayout.Controls.Add(_startMenuShortcutBox);
                optionLayout.Controls.Add(_startupShortcutBox);
                optionLayout.Controls.Add(_launchAfterInstallBox);
                optionPanel.Controls.Add(optionLayout);
                root.Controls.Add(optionPanel, 0, 2);

                _logBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = Color.FromArgb(17, 24, 39),
                    ForeColor = Color.FromArgb(209, 213, 219),
                    Font = SetupFonts.Regular(9f)
                };
                root.Controls.Add(_logBox, 0, 3);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                _installButton = new Button { Text = "Install", Width = 90, Height = 32 };
                _installButton.Click += delegate { Install(); };
                var closeButton = new Button { Text = "Close", Width = 90, Height = 32 };
                closeButton.Click += delegate { Close(); };
                buttons.Controls.Add(_installButton);
                buttons.Controls.Add(closeButton);
                root.Controls.Add(buttons, 0, 4);

                Log("Ready");
            }

            private void BrowseInstallPath()
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Choose the install folder.";
                    dialog.SelectedPath = _installPathBox.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        _installPathBox.Text = dialog.SelectedPath;
                    }
                }
            }

            private void Install()
            {
                try
                {
                    _installButton.Enabled = false;
                    var sourceApp = FindSourceApp();
                    var installDir = _installPathBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(installDir))
                    {
                        throw new InvalidOperationException("Enter an install location.");
                    }

                    Directory.CreateDirectory(installDir);
                    StopRunningApplications(installDir);
                    RemovePreviousAppFiles(installDir, sourceApp);
                    var installedAppPath = Path.Combine(installDir, Path.GetFileName(sourceApp));
                    File.Copy(sourceApp, installedAppPath, true);
                    Log("Installed app: " + installedAppPath);

                    CopyIfExists("README.md", installDir);
                    CopyIfExists("version.ini", installDir);
                    CopyIfExists("Uninstall.exe", installDir);
                    CopyFontsIfExists(installDir);
                    WriteInstallInfo(installDir);

                    if (_desktopShortcutBox.Checked)
                    {
                        CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName, installedAppPath, installDir, SetupProgram.ProductName);
                        Log("Created desktop shortcut");
                    }

                    if (_startMenuShortcutBox.Checked)
                    {
                        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ProductName);
                        Directory.CreateDirectory(startMenu);
                        CreateShortcut(startMenu, ShortcutName, installedAppPath, installDir, SetupProgram.ProductName);
                        var uninstallPath = Path.Combine(installDir, "Uninstall.exe");
                        if (File.Exists(uninstallPath))
                        {
                            CreateShortcut(startMenu, UninstallShortcutName, uninstallPath, installDir, SetupProgram.ProductName + " Uninstall");
                        }
                        Log("Created Start Menu shortcut");
                    }

                    if (_startupShortcutBox.Checked)
                    {
                        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                        CreateShortcut(startup, ShortcutName, installedAppPath, installDir, SetupProgram.ProductName + " Startup Reconnect", StartupArgument);
                        Log("Created Windows startup shortcut");
                    }

                    MessageBox.Show("Installation complete.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    if (_launchAfterInstallBox.Checked)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = installedAppPath,
                            WorkingDirectory = installDir,
                            UseShellExecute = true
                        });
                        Close();
                    }
                }
                catch (Exception error)
                {
                    Log("Install failed: " + error.Message);
                    MessageBox.Show("Installation failed.\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _installButton.Enabled = true;
                }
            }

            private void StopRunningApplications(string installDir)
            {
                var running = Process.GetProcesses()
                    .Where(IsSmbManagerApplicationProcess)
                    .ToList();

                if (running.Count == 0)
                {
                    return;
                }

                try
                {
                    Log("실행 중인 SMB Manager 종료 요청: " + running.Count + "개");
                    foreach (var process in running)
                    {
                        RequestGracefulExit(process);
                    }

                    WaitForProcessesToExit(running, GracefulExitWaitMilliseconds);
                    foreach (var process in running.Where(process => !HasExited(process)))
                    {
                        Log("정상 종료 대기 시간 초과, 프로세스 강제 종료: " + process.Id);
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }

                    WaitForProcessesToExit(running, ForcedExitWaitMilliseconds);
                    var remaining = running.FirstOrDefault(process => !HasExited(process));
                    if (remaining != null)
                    {
                        throw new InvalidOperationException("실행 중인 SMB Manager를 종료할 수 없습니다. 작업 관리자에서 프로세스를 종료한 뒤 다시 시도하세요.");
                    }

                    Log("실행 중인 SMB Manager 종료 완료");
                }
                finally
                {
                    foreach (var process in running)
                    {
                        process.Dispose();
                    }
                }
            }

            private static bool IsSmbManagerApplicationProcess(Process process)
            {
                try
                {
                    if (process.Id == Process.GetCurrentProcess().Id || process.HasExited)
                    {
                        return false;
                    }

                    var executablePath = process.MainModule.FileName;
                    var executableName = Path.GetFileName(executablePath);
                    return executableName.StartsWith(SetupProgram.ProductName + " V", StringComparison.OrdinalIgnoreCase)
                        && executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private static void RequestGracefulExit(Process process)
            {
                if (InstallerExitMessage != 0)
                {
                    var targetProcessId = (uint)process.Id;
                    EnumWindows(delegate(IntPtr windowHandle, IntPtr parameter)
                    {
                        uint windowProcessId;
                        GetWindowThreadProcessId(windowHandle, out windowProcessId);
                        if (windowProcessId == targetProcessId)
                        {
                            PostMessage(windowHandle, InstallerExitMessage, IntPtr.Zero, IntPtr.Zero);
                        }
                        return true;
                    }, IntPtr.Zero);
                }

                try
                {
                    process.CloseMainWindow();
                }
                catch
                {
                }
            }

            private static void WaitForProcessesToExit(System.Collections.Generic.IEnumerable<Process> processes, int timeoutMilliseconds)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                while (processes.Any(process => !HasExited(process)) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
            }

            private static bool HasExited(Process process)
            {
                try
                {
                    process.Refresh();
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }

            private static string FindSourceApp()
            {
                var setupDir = AppDomain.CurrentDomain.BaseDirectory;
                var files = Directory.GetFiles(setupDir, SetupProgram.ProductName + " V*.exe")
                    .Where(path => !Path.GetFileName(path).Equals("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .ToList();

                if (files.Count == 0)
                {
                    throw new FileNotFoundException("Place Setup.exe and the SMB Manager app exe in the same folder.");
                }

                return files[0];
            }

            private static string GetDefaultInstallPath()
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir))
                {
                    baseDir = Path.GetTempPath();
                }

                return Path.Combine(baseDir, "Programs", AppFolderName);
            }

            private static void CopyIfExists(string fileName, string installDir)
            {
                var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(installDir, fileName), true);
                }
            }

            private static void RemovePreviousAppFiles(string installDir, string sourceApp)
            {
                var sourcePath = Path.GetFullPath(sourceApp);
                foreach (var file in Directory.GetFiles(installDir, SetupProgram.ProductName + " V*.exe"))
                {
                    try
                    {
                        if (Path.GetFullPath(file).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }

            private static void CopyFontsIfExists(string installDir)
            {
                var sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts");
                if (!Directory.Exists(sourceDir))
                {
                    return;
                }

                var targetDir = Path.Combine(installDir, "Fonts");
                Directory.CreateDirectory(targetDir);
                foreach (var source in Directory.GetFiles(sourceDir))
                {
                    File.Copy(source, Path.Combine(targetDir, Path.GetFileName(source)), true);
                }
            }

            private static void WriteInstallInfo(string installDir)
            {
                var infoPath = Path.Combine(installDir, "install-info.ini");
                var text = "Product=" + SetupProgram.ProductName + "\r\nInstalledAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\nInstallDir=" + installDir + "\r\n";
                File.WriteAllText(infoPath, text, Encoding.UTF8);
            }

            private static void CreateShortcut(string folder, string shortcutName, string targetPath, string workingDirectory, string description)
            {
                CreateShortcut(folder, shortcutName, targetPath, workingDirectory, description, string.Empty);
            }

            private static void CreateShortcut(string folder, string shortcutName, string targetPath, string workingDirectory, string description, string arguments)
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return;
                }

                Directory.CreateDirectory(folder);
                var shortcutPath = Path.Combine(folder, shortcutName);
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
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { arguments ?? string.Empty });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            }

            private void Log(string message)
            {
                _logBox.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine + _logBox.Text;
            }
        }
    }
}
