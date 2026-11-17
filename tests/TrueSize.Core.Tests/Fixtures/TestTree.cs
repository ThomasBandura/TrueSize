using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using TrueSize.Core.Interop;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Tests.Fixtures;

/// <summary>
/// Creates a directory tree with hard links, junction, symlinks, ADS, sparse, compressed and WOF files.
/// Layout (relative to <see cref="Root"/>):
///   plain.bin                 10_000 random bytes
///   dirA\a1.bin                5_000 random bytes
///   dirA\linked.bin           21_000 random bytes (hard link, second link dirB\linked-copy.bin; extended via dirB after linking)
///   dirB\linked-copy.bin      hard link to dirA\linked.bin
///   dirC\external.bin         30_000 random bytes, second hard link outside the root
///   ads.txt                   100 bytes + stream "extra" with 3_000 bytes
///   sparse.bin                64 MiB logical, 4 KiB written
///   junction                  junction to dirA
///   symdir, symfile.bin       symbolic links (only if the privilege is available)
///   empty\                    empty directory
///   denied\inner.bin          directory with an ACL that denies listing (only if ACL could be set)
///   compressed.bin            1 MiB compressible, NTFS compression (NTFS only)
///   wof.bin                   1 MiB compressible, compact /exe:xpress4k (if compact.exe succeeds)
/// </summary>
public sealed class TestTree : IDisposable
{
    public const int PlainSize = 10_000;
    public const int A1Size = 5_000;
    public const int LinkedSize = 21_000;
    public const int ExternalSize = 30_000;
    public const int AdsMainSize = 100;
    public const int AdsStreamSize = 3_000;
    public const long SparseSize = 64L * 1024 * 1024;
    public const int CompressibleSize = 1024 * 1024;

    private readonly string _base;

    private TestTree(string baseDirectory, string fileSystem)
    {
        FileSystem = fileSystem;
        _base = Path.Combine(baseDirectory, "ts-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Root = Path.Combine(_base, "root");
        Outside = Path.Combine(_base, "outside");
    }

    public string Root { get; }

    public string Outside { get; }

    public string FileSystem { get; }

    public bool HasSymlinks { get; private set; }

    public bool HasCompression { get; private set; }

    public bool HasWof { get; private set; }

    public bool HasDeniedDirectory { get; private set; }

    public static TestTree Create(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        var tree = new TestTree(baseDirectory, VolumeInfo.FromPath(baseDirectory).FileSystem);
        tree.Build();
        return tree;
    }

    private void Build()
    {
        var rng = new Random(42);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Outside);
        Directory.CreateDirectory(P("dirA"));
        Directory.CreateDirectory(P("dirB"));
        Directory.CreateDirectory(P("dirC"));
        Directory.CreateDirectory(P("empty"));

        WriteRandom(P("plain.bin"), PlainSize, rng);
        WriteRandom(P(@"dirA\a1.bin"), A1Size, rng);

        // Hard link inside the tree; extend the file through the second link so the dirA directory entry may hold a stale size.
        WriteRandom(P(@"dirA\linked.bin"), LinkedSize - 1_000, rng);
        CreateHardLink(P(@"dirB\linked-copy.bin"), P(@"dirA\linked.bin"));
        using (var fs = new FileStream(P(@"dirB\linked-copy.bin"), FileMode.Append))
        {
            var extra = new byte[1_000];
            rng.NextBytes(extra);
            fs.Write(extra);
        }

        // Hard link leaving the tree.
        WriteRandom(P(@"dirC\external.bin"), ExternalSize, rng);
        CreateHardLink(Path.Combine(Outside, "external-link.bin"), P(@"dirC\external.bin"));

        // Alternate data stream.
        File.WriteAllBytes(P("ads.txt"), new byte[AdsMainSize]);
        File.WriteAllBytes(P("ads.txt") + ":extra", new byte[AdsStreamSize]);

        CreateSparse(P("sparse.bin"));

        RunCmd($"mklink /J \"{P("junction")}\" \"{P("dirA")}\"");

        try
        {
            Directory.CreateSymbolicLink(P("symdir"), P("dirA"));
            File.CreateSymbolicLink(P("symfile.bin"), P("plain.bin"));
            HasSymlinks = true;
        }
        catch (IOException)
        {
            // Requires Developer Mode or SeCreateSymbolicLinkPrivilege.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (FileSystem == "NTFS")
        {
            WriteCompressible(P("compressed.bin"));
            HasCompression = SetCompression(P("compressed.bin"));
        }

        WriteCompressible(P("wof.bin"));
        HasWof = RunProcess("compact.exe", $"/c /exe:xpress4k \"{P("wof.bin")}\"") == 0 &&
                 (File.GetAttributes(P("wof.bin")) & FileAttributes.ReparsePoint) != 0;

        // Enough entries to force a non-resident directory index ($INDEX_ALLOCATION on NTFS).
        Directory.CreateDirectory(P("bigdir"));
        for (int i = 0; i < BigDirFileCount; i++)
        {
            File.WriteAllBytes(P($@"bigdir\entry-with-a-reasonably-long-name-{i:D5}.dat"), []);
        }

        CreateDeniedDirectory();
    }

    public const int BigDirFileCount = 400;

    public string P(string relative) => Path.Combine(Root, relative);

    private static void WriteRandom(string path, int size, Random rng)
    {
        var data = new byte[size];
        rng.NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static void WriteCompressible(string path)
    {
        var data = new byte[CompressibleSize];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)('A' + (i % 7));
        }

        File.WriteAllBytes(path, data);
    }

    private static void CreateHardLink(string link, string existing)
    {
        if (!Native.CreateHardLink(link, existing, 0))
        {
            throw new IOException($"CreateHardLink failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }

    private static unsafe void CreateSparse(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite);
        if (!Native.DeviceIoControl(handle, Native.FSCTL_SET_SPARSE, null, 0, null, 0, out _, 0))
        {
            throw new IOException($"FSCTL_SET_SPARSE failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }

        var data = new byte[4096];
        Array.Fill(data, (byte)0x5A);
        RandomAccess.Write(handle, data, 0);
        RandomAccess.SetLength(handle, SparseSize);
    }

    private static unsafe bool SetCompression(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        ushort format = Native.COMPRESSION_FORMAT_DEFAULT;
        return Native.DeviceIoControl(handle, Native.FSCTL_SET_COMPRESSION, &format, sizeof(ushort), null, 0, out _, 0);
    }

    private void CreateDeniedDirectory()
    {
        string denied = P("denied");
        Directory.CreateDirectory(denied);
        File.WriteAllBytes(Path.Combine(denied, "inner.bin"), new byte[1234]);

        if (IsElevated())
        {
            // Backup semantics would bypass the ACL for administrators; the scenario is not reproducible.
            return;
        }

        var info = new DirectoryInfo(denied);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
        HasDeniedDirectory = true;
    }

    private void RemoveDeny()
    {
        if (!HasDeniedDirectory)
        {
            return;
        }

        var info = new DirectoryInfo(P("denied"));
        var security = info.GetAccessControl();
        security.RemoveAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RunCmd(string command)
    {
        if (RunProcess("cmd.exe", "/c " + command) != 0)
        {
            throw new IOException("Command failed: " + command);
        }
    }

    private static int RunProcess(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            RemoveDeny();

            // Delete links first so recursive deletion never walks into link targets.
            foreach (string link in new[] { "junction", "symdir" })
            {
                if (Directory.Exists(P(link)))
                {
                    Directory.Delete(P(link));
                }
            }

            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
