using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace MdQuiz.Wpf
{
    /// <summary>
    /// 复制文字到剪贴板。WPF 的 Clipboard.SetText 在剪贴板正被其他程序占用时（输入法、剪贴板工具、
    /// 远程桌面、聊天软件等）会抛出 CLIPBRD_E_CANT_OPEN；这里直接调用 Win32 接口并多次重试。
    /// </summary>
    public static class ClipboardText
    {
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;
        private const int Attempts = 20;
        private const int RetryDelayMs = 60;

        /// <summary>复制成功返回 true；剪贴板一直被占用时返回 false（不抛异常）。必须在界面线程调用。</summary>
        public static bool TrySet(string text)
        {
            text = text ?? "";
            for (var i = 0; i < Attempts; i++)
            {
                if (TrySetOnce(text)) return true;
                Thread.Sleep(RetryDelayMs);
            }
            // 最后再试一次 WPF 的方式（不立即 Flush，减少与其他程序争用），并确认内容确实已写入
            try
            {
                Clipboard.SetDataObject(text, false);
            }
            catch (Exception)
            {
                // 忽略，下面检查结果
            }
            try
            {
                return Clipboard.ContainsText() && Clipboard.GetText() == text;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TrySetOnce(string text)
        {
            var owner = OwnerHandle();
            // OpenClipboard(NULL) 后 SetClipboardData 会失败，必须提供窗口句柄
            if (owner == IntPtr.Zero || !OpenClipboard(owner)) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                var bytes = (text.Length + 1) * 2;
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                if (memory == IntPtr.Zero) return false;
                var target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(memory);
                }
                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return false;
                }
                // 成功后内存归系统所有，不能释放
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        private static IntPtr OwnerHandle()
        {
            try
            {
                var window = Ui.ActiveWindow ?? Application.Current?.MainWindow;
                return window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
