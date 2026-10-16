using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SmbManagerUninstall
{
    internal static class UninstallProgram
    {
        private const string ProductName = "SMB Manager";
        private const string ShortcutName = "SMB Manager.lnk";
        private const string UninstallShortcutName = "SMB Manager Uninstall.lnk";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UninstallForm());
        }

        private sealed class UninstallForm : Form
        {
            private const int GracefulExitWaitMilliseconds = 4000;
            private const int ForcedExitWaitMilliseconds = 5000;
            private static readonly uint InstallerExitMessage = RegisterWindowMessage("SmbManager.Setup.RequestExit");
            private readonly CheckBox _removeUserDataBox;
            private readonly TextBox _logBox;
            private readonly Button _uninstallButton;
            private readonly string _runDir = AppDomain.CurrentDomain.BaseDirectory;
            private readonly string _installDir = ResolveInstallDirectory();

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

            public UninstallForm()
            {
                Text = ProductName + " Uninstall";
                StartPosition = FormStartPosition.CenterScreen;
                MinimumSize = new Size(520, 360);
                Size = new Size(600, 420);
                Font = new Font("Noto Sans CJK KR", 9f, FontStyle.Regular, GraphicsUnit.Point);
                BackColor = Color.FromArgb(243, 243, 243);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    ColumnCount = 1,
                    RowCount = 4
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
                Controls.Add(root);

                root.Controls.Add(new Label
                {
                    Text = ProductName + " 제거",
                    Dock = DockStyle.Fill,
                    Font = new Font(Font, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleLeft
                }, 0, 0);

                var options = new GroupBox { Text = "제거 옵션", Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };
                _removeUserDataBox = new CheckBox
                {
                    Text = "사용자 설정, 진단 로그, 저장된 비밀번호도 삭제",
                    AutoSize = true,
                    Dock = DockStyle.Top
                };
                options.Controls.Add(_removeUserDataBox);
                root.Controls.Add(options, 0, 1);

                _logBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = Color.FromArgb(17, 24, 39),
                    ForeColor = Color.FromArgb(230, 230, 230)
                };
                root.Controls.Add(_logBox, 0, 2);

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
                _uninstallButton = new Button { Text = "제거", Width = 90, Height = 32 };
                _uninstallButton.Click += delegate { Uninstall(); };
                var closeButton = new Button { Text = "닫기", Width = 90, Height = 32 };
                closeButton.Click += delegate { Close(); };
                buttons.Controls.Add(_uninstallButton);
                buttons.Controls.Add(closeButton);
                root.Controls.Add(buttons, 0, 3);

                Log("실행 경로: " + _runDir);
                Log("제거 대상 설치 경로: " + _installDir);
            }

            private void Uninstall()
            {
                if (MessageBox.Show("SMB Manager를 제거할까요?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                try
                {
                    _uninstallButton.Enabled = false;
                    StopRunningApplications();
                    RemoveShortcuts();
                    RemoveUpdateInstallRoot();

                    if (_removeUserDataBox.Checked)
                    {
                        RemoveUserData();
                    }

                    ScheduleInstallDirectoryRemoval();
                    MessageBox.Show("제거 작업을 예약했습니다.\r\n창을 닫으면 설치 폴더가 삭제됩니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                }
                catch (Exception error)
                {
                    Log("제거 실패: " + error.Message);
                    MessageBox.Show("제거 중 오류가 발생했습니다.\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    _uninstallButton.Enabled = true;
                }
            }

            private void StopRunningApplications()
            {
                var roots = new List<string>();
                AddProcessRoot(roots, _installDir);
                AddProcessRoot(roots, GetUpdateInstallRoot());

                var running = new List<Process>();
                foreach (var process in Process.GetProcesses())
                {
                    if (IsApplicationProcess(process, roots))
                    {
                        running.Add(process);
                    }
                    else
                    {
                        process.Dispose();
                    }
                }

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
                    foreach (var process in running)
                    {
                        if (HasExited(process))
                        {
                            continue;
                        }

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
                    foreach (var process in running)
                    {
                        if (!HasExited(process))
                        {
                            throw new InvalidOperationException("실행 중인 SMB Manager를 종료할 수 없습니다. 작업 관리자에서 프로세스를 종료한 뒤 다시 시도하세요.");
                        }
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

            private static void AddProcessRoot(List<string> roots, string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        return;
                    }

                    var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!roots.Exists(root => root.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    {
                        roots.Add(normalized);
                    }
                }
                catch
                {
                }
            }

            private static bool IsApplicationProcess(Process process, List<string> roots)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return false;
                    }

                    var executablePath = Path.GetFullPath(process.MainModule.FileName);
                    var executableName = Path.GetFileName(executablePath);
                    if (!executableName.StartsWith(UninstallProgram.ProductName + " V", StringComparison.OrdinalIgnoreCase)
                        || !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    foreach (var root in roots)
                    {
                        if (executablePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }

                return false;
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

            private static void WaitForProcessesToExit(List<Process> processes, int timeoutMilliseconds)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                while (DateTime.UtcNow < deadline)
                {
                    var anyRunning = false;
                    foreach (var process in processes)
                    {
                        if (!HasExited(process))
                        {
                            anyRunning = true;
                            break;
                        }
                    }

                    if (!anyRunning)
                    {
                        return;
                    }

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

            private void RemoveShortcuts()
            {
                DeleteFileIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName));
                DeleteFileIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName));

                var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ProductName);
                DeleteFileIfExists(Path.Combine(startMenu, ShortcutName));
                DeleteFileIfExists(Path.Combine(startMenu, UninstallShortcutName));
                TryDeleteDirectory(startMenu);
                Log("바로가기 정리 완료");
            }

            private void RemoveUpdateInstallRoot()
            {
                var updateRoot = GetUpdateInstallRoot();
                if (string.IsNullOrWhiteSpace(updateRoot))
                {
                    return;
                }

                TryDeleteDirectory(updateRoot);
                Log("업데이트 설치 폴더 정리 완료");
            }

            private static string GetUpdateInstallRoot()
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrWhiteSpace(baseDir) ? string.Empty : Path.Combine(baseDir, UninstallProgram.ProductName, "Installed");
            }

            private void RemoveUserData()
            {
                TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName));
                TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName));
                Log("사용자 데이터 정리 완료");
            }

            private void ScheduleInstallDirectoryRemoval()
            {
                var installDir = Path.GetFullPath(_installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(installDir) || installDir.Length < 10)
                {
                    return;
                }

                var marker = Path.Combine(installDir, "install-info.ini");
                var hasApp = Directory.GetFiles(installDir, ProductName + " V*.exe").Length > 0;
                if (!File.Exists(marker) && !hasApp)
                {
                    throw new InvalidOperationException("설치 폴더로 확인되지 않아 폴더 삭제를 중단했습니다.");
                }

                var runDir = Path.GetFullPath(_runDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!installDir.Equals(runDir, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteDirectoryWithRetries(installDir);
                    Log("설치 폴더 삭제 완료: " + installDir);
                    return;
                }

                var command = "/c timeout /t 2 /nobreak >nul & rmdir /s /q " + QuoteForCmd(installDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = command,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Log("설치 폴더 삭제 예약: " + installDir);
            }

            private static void DeleteDirectoryWithRetries(string path)
            {
                Exception lastError = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        Directory.Delete(path, true);
                        return;
                    }
                    catch (IOException error)
                    {
                        lastError = error;
                    }
                    catch (UnauthorizedAccessException error)
                    {
                        lastError = error;
                    }

                    Thread.Sleep(300);
                }

                throw new IOException("설치 폴더를 삭제할 수 없습니다. 실행 중인 파일이 없는지 확인한 뒤 다시 시도하세요.", lastError);
            }

            private static string ResolveInstallDirectory()
            {
                var currentDir = AppDomain.CurrentDomain.BaseDirectory;
                var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", UninstallProgram.ProductName);
                if (IsInstallDirectory(defaultDir))
                {
                    return defaultDir;
                }

                if (IsInstallDirectory(currentDir))
                {
                    return currentDir;
                }

                var updatedDir = ResolveUpdatedInstallDirectory();
                if (!string.IsNullOrWhiteSpace(updatedDir) && Directory.Exists(updatedDir))
                {
                    return updatedDir;
                }

                return currentDir;
            }

            private static string ResolveUpdatedInstallDirectory()
            {
                try
                {
                    var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrWhiteSpace(baseDir))
                    {
                        return string.Empty;
                    }

                    var currentPath = Path.Combine(baseDir, UninstallProgram.ProductName, "Installed", "current.txt");
                    if (!File.Exists(currentPath))
                    {
                        return string.Empty;
                    }

                    var appPath = File.ReadAllText(currentPath, Encoding.UTF8).Trim();
                    if (File.Exists(appPath))
                    {
                        return Path.GetDirectoryName(appPath);
                    }
                }
                catch
                {
                }

                return string.Empty;
            }

            private static bool IsInstallDirectory(string path)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    {
                        return false;
                    }

                    var marker = Path.Combine(path, "install-info.ini");
                    var hasApp = Directory.GetFiles(path, UninstallProgram.ProductName + " V*.exe").Length > 0;
                    return File.Exists(marker) || hasApp;
                }
                catch
                {
                    return false;
                }
            }

            private static void DeleteFileIfExists(string path)
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

            private static void TryDeleteDirectory(string path)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
                catch
                {
                }
            }

            private static string QuoteForCmd(string value)
            {
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            }

            private void Log(string message)
            {
                _logBox.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine + _logBox.Text;
            }
        }
    }
}
