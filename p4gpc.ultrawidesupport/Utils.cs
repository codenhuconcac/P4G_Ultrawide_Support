using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p4gpc.ultrawidesupport
{
    internal class Utils
    {
        private static ILogger _logger;
        private static IStartupScanner _startupScanner;
        internal static nint BaseAddress { get; private set; }

        internal static bool Initialise(ILogger logger, IModLoader modLoader)
        {
            _logger = logger;
            using var thisProcess = Process.GetCurrentProcess();
            BaseAddress = thisProcess.MainModule!.BaseAddress;

            var startupScannerController = modLoader.GetController<IStartupScanner>();
            if (startupScannerController == null || !startupScannerController.TryGetTarget(out _startupScanner))
            {
                LogErrorDebugOnly("unable to get controller for reloaded sigscan lib");
                return false;
            }

            return true;
        }

        [Conditional("DEBUG")]
        internal static void LogDebug(string message)
        {
            _logger.WriteLine($"[k8pc] {message}");
        }

        [Conditional("DEBUG")]
        internal static void Log(string message)
        {
            _logger.WriteLine($"[k8pc] {message}");
        }

        [Conditional("DEBUG")]
        internal static void LogError(string message, Exception e)
        {
            _logger.WriteLine($"[k8pc] {message}: {e.Message}", System.Drawing.Color.Red);
        }

        [Conditional("DEBUG")]
        internal static void LogError(string message)
        {
            _logger.WriteLine($"[k8pc] {message}", System.Drawing.Color.Red);
        }

        internal static void SigScan(string pattern, string name, Action<nint> action)
        {
            _startupScanner.AddMainModuleScan(pattern, result =>
            {
                if (!result.Found)
                {
                    //LogError($"{name} not found");
                    return;
                }
                //LogDebug($"found {name} at 0x{result.Offset + BaseAddress:X}");

                action(result.Offset + BaseAddress);
            });
        }

        internal static unsafe nuint GetGlobalAddress(nint ptrAddress)
        {
            return (nuint)((*(int*)ptrAddress) + ptrAddress + 4);
        }

        internal static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace(" ", "");

            byte[] bytes = new byte[hex.Length / 2];

            for (int i = 0; i < hex.Length; i += 2)
            {
                // Parse every 2 chars as a hex byte
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            // The last byte is automatically 0 (Null Terminator) because new byte[] inits with 0s.
            return bytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        // Cursor Resource IDs
        public const int IDC_ARROW = 32512;
        public const int IDC_HAND = 32649;

        public static bool IsLeftMousePressed()
        {
            // 0x01 is VK_LBUTTON
            // The & 0x8000 checks if the key is currently down
            return (GetAsyncKeyState(0x01) & 0x8000) != 0;
        }

        [Conditional("DEBUG")]
        internal static void LogDebugOnly(string message)
        {
            _logger.WriteLine(EnsurePcPrefix(message));
        }

        [Conditional("DEBUG")]
        internal static void LogErrorDebugOnly(string message)
        {
            _logger.WriteLine(EnsurePcPrefix(message), System.Drawing.Color.Red);
        }

        [Conditional("DEBUG")]
        internal static void LogErrorDebugOnly(string message, Exception e)
        {
            _logger.WriteLine($"{EnsurePcPrefix(message)}: {e.Message}", System.Drawing.Color.Red);
        }

        private static string EnsurePcPrefix(string message)
        {
            if (string.IsNullOrEmpty(message)) return "[k8pc]";
            if (message.StartsWith("[k8pc", StringComparison.Ordinal) ||
                message.StartsWith("[k8shared", StringComparison.Ordinal) ||
                message.StartsWith("[k8nx", StringComparison.Ordinal))
                return message;
            if (message[0] == '[')
            {
                int close = message.IndexOf(']');
                if (close > 1)
                    return $"[k8pc|{message.Substring(1, close - 1)}]" +
                           message.Substring(close + 1);
            }
            return $"[k8pc] {message}";
        }
    }
}
