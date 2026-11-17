using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueSize.Core.Interop;

// Verified against Windows SDK 10.0.26100.0: winnt.h, ntdef.h, securitybaseapi.h, WinBase.h.
internal static unsafe partial class Privileges
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300; // winerror.h

    private static int s_backupEnabled; // 0 = not tried, 1 = enabled, -1 = failed

    /// <summary>ntdef.h LUID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>winnt.h TOKEN_PRIVILEGES with exactly one LUID_AND_ATTRIBUTES.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES_1
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, TOKEN_PRIVILEGES_1* newState, uint bufferLength, void* previousState, void* returnLength);

    /// <summary>
    /// Enables SeBackupPrivilege for the process (elevated only). Together with FILE_FLAG_BACKUP_SEMANTICS this lets
    /// the directory scanners read every directory, matching what the MFT scanner sees.
    /// </summary>
    public static bool TryEnableBackupPrivilege()
    {
        int state = Volatile.Read(ref s_backupEnabled);
        if (state != 0)
        {
            return state > 0;
        }

        bool ok = Enable("SeBackupPrivilege");
        Volatile.Write(ref s_backupEnabled, ok ? 1 : -1);
        return ok;
    }

    private static bool Enable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return false;
            }

            var tp = new TOKEN_PRIVILEGES_1 { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };

            // AdjustTokenPrivileges succeeds even if the privilege is not held; ERROR_NOT_ALL_ASSIGNED reports that.
            return AdjustTokenPrivileges(token, false, &tp, (uint)sizeof(TOKEN_PRIVILEGES_1), null, null) &&
                   Marshal.GetLastPInvokeError() != ERROR_NOT_ALL_ASSIGNED;
        }
    }
}
