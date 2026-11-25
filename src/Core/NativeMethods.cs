using System;
using System.Runtime.InteropServices;

namespace RcloneOssManager
{
    /// <summary>窗口置前、圆角与标题栏配色所需的Win32互操作；不涉及文件系统或网络。</summary>
    internal static class NativeMethods
    {
        internal const int SwRestore = 9;
        internal const int SwShow = 5;
        internal const int AsfwAny = -1;
        internal const uint WmNull = 0x0000;
        internal const int DwmwaBorderColor = 34;
        internal const int DwmwaCaptionColor = 35;
        internal const int DwmwaTextColor = 36;
        internal const int DwmwaCornerPreference = 33;
        internal const int DwmcpRound = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>把颜色打包成DWM使用的COLORREF（0x00BBGGRR）。</summary>
        /// <param name="color">界面颜色。</param>
        /// <returns>COLORREF整数。</returns>
        internal static int ColorRef(System.Drawing.Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        /// <summary>按DWM约定设置窗口属性；系统不支持该属性时静默忽略，绝不因此阻止窗口显示。</summary>
        /// <param name="hwnd">窗口句柄。</param><param name="attribute">属性编号。</param><param name="value">属性值。</param>
        internal static void TrySetWindowAttribute(IntPtr hwnd, int attribute, int value)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var local = value;
                DwmSetWindowAttribute(hwnd, attribute, ref local, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (SEHException) { }
        }

        /// <summary>把窗口恢复到前台：还原最小化、置顶并交还焦点，失败时再尝试附加输入队列。</summary>
        /// <param name="hwnd">目标窗口句柄。</param><returns>是否已发起置前。</returns>
        internal static bool ActivateWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                else ShowWindow(hwnd, SwShow);
                BringWindowToTop(hwnd);
                if (SetForegroundWindow(hwnd)) return true;

                // 前台锁：临时把本线程与当前前台窗口的输入队列合并后再置前。
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;
                var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
                var currentThread = GetCurrentThreadId();
                if (foregroundThread == 0 || foregroundThread == currentThread) return false;
                if (!AttachThreadInput(currentThread, foregroundThread, true)) return false;
                try { return SetForegroundWindow(hwnd); }
                finally { AttachThreadInput(currentThread, foregroundThread, false); }
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (SEHException) { return false; }
        }

        // ---- 令牌提升检测（advapi32/kernel32）：判断进程是否以管理员令牌运行 ----

        internal const int TokenQuery = 0x0008;
        internal const int TokenElevation = 20;

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int classId, out int info, int length, out int returnLength);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
