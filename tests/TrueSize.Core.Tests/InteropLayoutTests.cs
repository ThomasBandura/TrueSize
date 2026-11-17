using System.Runtime.InteropServices;
using TrueSize.Core.Interop;

namespace TrueSize.Core.Tests;

/// <summary>Locks struct layouts to the offsets implied by the Windows SDK 10.0.26100.0 headers (x64).</summary>
public sealed class InteropLayoutTests
{
    [Fact]
    public void FileIdExtdDirInfo_Layout()
    {
        Assert.Equal(56, Marshal.OffsetOf<FILE_ID_EXTD_DIR_INFO>(nameof(FILE_ID_EXTD_DIR_INFO.FileAttributes)).ToInt32());
        Assert.Equal(68, Marshal.OffsetOf<FILE_ID_EXTD_DIR_INFO>(nameof(FILE_ID_EXTD_DIR_INFO.ReparsePointTag)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<FILE_ID_EXTD_DIR_INFO>(nameof(FILE_ID_EXTD_DIR_INFO.FileId)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<FILE_ID_EXTD_DIR_INFO>(nameof(FILE_ID_EXTD_DIR_INFO.FileName)).ToInt32());
    }

    [Fact]
    public unsafe void FixedSizeStructs_Sizes()
    {
        Assert.Equal(24, sizeof(FILE_STANDARD_INFO));
        Assert.Equal(16, sizeof(FILE_COMPRESSION_INFO));
        Assert.Equal(24, Marshal.OffsetOf<FILE_STREAM_INFO>(nameof(FILE_STREAM_INFO.StreamName)).ToInt32());
        Assert.Equal(104, sizeof(FILE_STAT_BASIC_INFORMATION));
        Assert.Equal(64, Marshal.OffsetOf<FILE_STAT_BASIC_INFORMATION>(nameof(FILE_STAT_BASIC_INFORMATION.NumberOfLinks)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<FILE_STAT_BASIC_INFORMATION>(nameof(FILE_STAT_BASIC_INFORMATION.FileId128)).ToInt32());
    }

    [Fact]
    public void IoctlCodes_MatchCtlCodeMacro()
    {
        // CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, 49, METHOD_BUFFERED, FILE_SPECIAL_ACCESS=0)
        Assert.Equal(0x000900C4u, Native.FSCTL_SET_SPARSE);

        // CTL_CODE(9, 16, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA = 3)
        Assert.Equal(0x0009C040u, Native.FSCTL_SET_COMPRESSION);
    }
}
