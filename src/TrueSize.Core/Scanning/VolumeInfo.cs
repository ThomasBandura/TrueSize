using System.ComponentModel;
using System.Runtime.InteropServices;
using TrueSize.Core.Interop;

namespace TrueSize.Core.Scanning;

public sealed record VolumeInfo(string Root, uint SerialNumber, string FileSystem)
{
    public static unsafe VolumeInfo FromPath(string path)
    {
        char* root = stackalloc char[1024];
        if (!Native.GetVolumePathName(path, root, 1024))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetVolumePathName failed for '{path}'.");
        }

        string rootPath = new(root);
        char* fsName = stackalloc char[64];
        if (!Native.GetVolumeInformation(rootPath, null, 0, out uint serial, out _, out _, fsName, 64))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetVolumeInformation failed for '{rootPath}'.");
        }

        return new VolumeInfo(rootPath, serial, new string(fsName));
    }
}
