using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deep.Client.Maui.StrictGates
{
    public static class WindowsInteractiveSessionProbe
    {
        private const int WtsInfoEx = 25;
        private const int WtsInfoExLevel1 = 1;
        private const int WtsSessionStateLock = 0;
        private const int WtsSessionStateUnlock = 1;

        public static bool IsCurrentSessionUnlocked()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return false;
            }

            IntPtr buffer;
            int bytesReturned;
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    Process.GetCurrentProcess().SessionId,
                    WtsInfoEx,
                    out buffer,
                    out bytesReturned))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var unionOffset = IntPtr.Size == 8 ? 8 : 4;
                var sessionFlagsOffset = unionOffset + 8;
                if (bytesReturned < sessionFlagsOffset + sizeof(int))
                {
                    throw new InvalidOperationException("The WTS session response was truncated.");
                }

                var level = Marshal.ReadInt32(buffer);
                if (level != WtsInfoExLevel1)
                {
                    throw new InvalidOperationException("The WTS session response used an unsupported level.");
                }

                var sessionFlags = Marshal.ReadInt32(buffer, sessionFlagsOffset);
                return IsUnlockedSessionFlag(sessionFlags, Environment.OSVersion.Version);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        public static bool IsUnlockedSessionFlag(int sessionFlags, Version windowsVersion)
        {
            if (windowsVersion == null)
            {
                throw new ArgumentNullException("windowsVersion");
            }

            // Windows 7 / Server 2008 R2 report the two documented flags in reverse.
            var reversedLegacyFlags = windowsVersion.Major == 6 && windowsVersion.Minor == 1;
            var unlockedFlag = reversedLegacyFlags ? WtsSessionStateLock : WtsSessionStateUnlock;
            return sessionFlags == unlockedFlag;
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr serverHandle,
            int sessionId,
            int infoClass,
            out IntPtr buffer,
            out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr buffer);
    }
}
