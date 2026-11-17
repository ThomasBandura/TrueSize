using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrueSize.Core.Interop;

// Verified against Windows SDK 10.0.26100.0: processthreadsapi.h, securitybaseapi.h, winnt.h.
internal static unsafe partial class Elevation
{
    private const uint TOKEN_QUERY = 0x0008;

    // winnt.h TOKEN_INFORMATION_CLASS: TokenUser = 1, ..., TokenElevation is the 20th member.
    private const int TokenElevation = 20;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, void* tokenInformation, uint tokenInformationLength, out uint returnLength);

    /// <summary>TOKEN_ELEVATION.TokenIsElevated of the current process token.</summary>
    public static bool IsProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        {
            uint elevated = 0; // TOKEN_ELEVATION { DWORD TokenIsElevated; }
            return GetTokenInformation(token, TokenElevation, &elevated, sizeof(uint), out _) && elevated != 0;
        }
    }
}
