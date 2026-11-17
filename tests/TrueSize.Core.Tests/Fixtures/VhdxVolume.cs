using System.Diagnostics;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Tests.Fixtures;

/// <summary>Skips the test unless the process is elevated.</summary>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        if (!ScannerSelector.IsElevated())
        {
            Skip = "Requires an elevated process.";
        }
    }
}

/// <summary>
/// Temporary NTFS volume: creates an expandable VHDX under %TEMP% with diskpart, formats it and assigns a drive letter.
/// Requires elevation. Detached and deleted on dispose.
/// </summary>
public sealed class VhdxVolume : IDisposable
{
    private readonly string _file;

    public VhdxVolume(int sizeMb = 256)
    {
        string id = Guid.NewGuid().ToString("N");
        _file = Path.Combine(Path.GetTempPath(), $"truesize-{id}.vhdx");

        // Unique label so leftovers from an aborted run can never be picked up by mistake.
        string Label = "TS" + id[..8].ToUpperInvariant();
        RunDiskpart(
            $"create vdisk file=\"{_file}\" maximum={sizeMb} type=expandable",
            $"select vdisk file=\"{_file}\"",
            "attach vdisk",
            "convert mbr",
            "create partition primary",
            $"format fs=ntfs quick label={Label}",
            "assign");

        Root = DriveInfo.GetDrives().Single(d => d.IsReady && d.VolumeLabel == Label && d.DriveFormat == "NTFS").RootDirectory.FullName;
    }

    /// <summary>Drive root, e.g. "R:\".</summary>
    public string Root { get; }

    public void Dispose()
    {
        try
        {
            RunDiskpart($"select vdisk file=\"{_file}\"", "detach vdisk");
            File.Delete(_file);
        }
        catch (Exception)
        {
            // Best effort; a leftover VHDX in %TEMP% is harmless.
        }
    }

    private static void RunDiskpart(params string[] commands)
    {
        string script = Path.Combine(Path.GetTempPath(), $"truesize-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(script, commands);
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"diskpart failed ({process.ExitCode}):\n{output}");
            }
        }
        finally
        {
            File.Delete(script);
        }
    }
}
