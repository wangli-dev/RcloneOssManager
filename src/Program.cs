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
    internal static class Program
    {
        private static Mutex singleInstance;
        private static EventWaitHandle showSignal;

        [STAThread]
        private static void Main(string[] argv)
        {
            try { UiStyle.IsDark = String.Equals(RuntimePaths.Current.Theme, "dark", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { UiStyle.IsDark = false; }
            var background = argv != null && argv.Any(x => String.Equals(x, "--background", StringComparison.OrdinalIgnoreCase));
            // 切换皮肤后旧实例拉起新进程，先等旧实例退出并释放单实例互斥体。
            if (argv != null && argv.Any(x => String.Equals(x, "--theme-restart", StringComparison.OrdinalIgnoreCase))) Thread.Sleep(2500);
            bool created;
            try { singleInstance = new Mutex(true, SingleInstanceChannel.MutexName(background), out created); }
            catch (UnauthorizedAccessException) { created = true; }
            if (!created)
            {
                // 已经有一个管理器在运行：把它的窗口提到前台，而不是静默退出。
                if (!background) SingleInstanceChannel.RequestShow();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                MessageBox.Show(ErrorText.ToChinese(args.Exception.Message), "OSS 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            if (background)
            {
                try { RcloneClient.RestoreRecordedMounts(); }
                catch (Exception ex) { ManagerLog.Write("开机恢复挂载失败", "", "", ex.Message); }
                return;
            }
            WarnIfElevated();
            showSignal = CreateShowSignal();
            if (argv != null && argv.Any(x => x == "--settings")) { using (var dialog = new RuntimeSettingsForm()) dialog.ShowDialog(); return; }
            try
            {
                var form = new MainForm(background);
                SingleInstanceChannel.StartWatcher(form, showSignal);
                Application.Run(form);
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(ErrorText.ToChinese(ex.Message) + "\r\n\r\n是否打开路径设置？保存后请重新启动程序。", "OSS 管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    using (var dialog = new RuntimeSettingsForm()) dialog.ShowDialog();
            }
        }

        /// <summary>创建唤醒信标；被占用或权限不足时返回空值，管理器仍可正常单开使用。</summary>
        /// <returns>命名事件或空值。</returns>
        private static EventWaitHandle CreateShowSignal()
        {
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceChannel.ShowEventName); }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>管理员令牌启动时弹窗提示：本地盘符只进管理员登录会话，标准权限的资源管理器不可见。</summary>
        /// <remarks>UAC 下管理员与标准令牌属于不同登录会话，WinFsp 本地盘模式的盘符仅对创建它的会话可见；双击快捷方式与开机自启均为标准令牌，只有从管理员终端启动才会踩中。检测失败时视为普通环境，不打断启动。</remarks>
        internal static void WarnIfElevated()
        {
            try
            {
                IntPtr token;
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenQuery, out token)) return;
                try
                {
                    int info;
                    int returnLength;
                    if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out info, sizeof(int), out returnLength)) return;
                    if (info == 0) return; // 标准令牌，直接放行。
                }
                finally { NativeMethods.CloseHandle(token); }
                var choice = MessageBox.Show(
                    "当前正以管理员身份运行。\r\n\r\n" +
                    "UAC 下管理员令牌挂载的本地盘符只对管理员会话可见，标准权限的资源管理器（含开机自启）将看不到挂载的盘符。\r\n\r\n" +
                    "建议改为普通方式启动（例如双击桌面快捷方式）。\r\n\r\n" +
                    "是否仍要继续？",
                    "OSS 管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (choice == DialogResult.No) Environment.Exit(0);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception) { }
        }
    }
}
