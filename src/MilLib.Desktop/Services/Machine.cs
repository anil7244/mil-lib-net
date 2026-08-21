using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MilLib.Desktop.Services;

/// <summary>
/// What machine this is.
///
/// Sixteen hex characters as XXXX-XXXX-XXXX-XXXX, from the motherboard serial
/// where there is one. A licence key is issued against this, so two things
/// matter more than anything else about it:
///
/// **It must match what the web application works out on the same machine.**
/// A unit may already hold a key issued for that installation, and it has to
/// keep working here. So this asks Windows the same questions in the same
/// order, and hashes the same raw text — not a tidied-up version of it.
///
/// **It is never written to disk.** An early version of the web application
/// cached it to a file under its storage folder, which meant copying the whole
/// folder to another machine carried the first machine's identity with it and
/// the copy ran licensed. The value is held for the life of the process and no
/// longer.
/// </summary>
public static class Machine
{
    private static string? _thisRun;

    public static string HardwareId => _thisRun ??= Format(Serial() ?? Fallback());

    /// <summary>
    /// Where the identity came from, for the screen to say. Somebody whose
    /// hardware ID has changed needs to know which of these answered.
    /// </summary>
    public static string Source { get; private set; } = "";

    /// <summary>
    /// The machine's own serial, from wherever this operating system keeps it.
    /// Windows is asked exactly as the web application asks it, so a key issued
    /// for that installation still matches; the other systems have their own
    /// stable identity, which is all a licence needs where there is no web
    /// application to agree with.
    /// </summary>
    private static string? Serial() =>
        OperatingSystem.IsWindows() ? WindowsSerial()
        : OperatingSystem.IsLinux() ? LinuxSerial()
        : OperatingSystem.IsMacOS() ? MacSerial()
        : null;

    private static string? WindowsSerial()
    {
        // The same three questions the web application asks, in the same
        // order. Changing the order would change the answer on a machine where
        // more than one of them replies, and the key would stop matching.
        (string Ask, string Called)[] questions =
        [
            ("(Get-CimInstance Win32_BaseBoard).SerialNumber", "the motherboard"),
            ("(Get-CimInstance Win32_BIOS).SerialNumber", "the BIOS"),
            ("(Get-CimInstance Win32_ComputerSystemProduct).UUID", "the system UUID"),
        ];

        foreach (var (ask, called) in questions)
        {
            var answer = Ask(ask);

            if (answer is not null)
            {
                Source = called;

                return answer;
            }
        }

        return null;
    }

    /// <summary>
    /// On Linux, the machine id the system keeps for exactly this purpose —
    /// world-readable, stable for the life of the install, and needing no root.
    /// The board serial is tried first for those machines that expose it, since
    /// it survives a reinstall.
    /// </summary>
    private static string? LinuxSerial()
    {
        (string Path, string Called)[] files =
        [
            ("/sys/class/dmi/id/board_serial", "the motherboard"),
            ("/sys/class/dmi/id/product_uuid", "the system UUID"),
            ("/etc/machine-id", "the machine id"),
            ("/var/lib/dbus/machine-id", "the machine id"),
        ];

        foreach (var (path, called) in files)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var value = File.ReadAllText(path).Trim();

                if (value.Length > 0 && !Meaningless(value))
                {
                    Source = called;

                    return value;
                }
            }
            catch (Exception)
            {
                // Unreadable (a serial file often needs root) — try the next.
            }
        }

        return null;
    }

    /// <summary>On macOS, the platform UUID the hardware reports through ioreg.</summary>
    private static string? MacSerial()
    {
        var output = Run("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice");

        if (output is null)
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("IOPlatformUUID"))
            {
                continue;
            }

            var parts = line.Split('"');

            if (parts.Length >= 4 && parts[3].Trim() is { Length: > 0 } uuid && !Meaningless(uuid))
            {
                Source = "the platform UUID";

                return uuid;
            }
        }

        return null;
    }

    /// <summary>Run a command and read its output, or null if it will not run.</summary>
    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Discarding the answer anyway.
                }

                return null;
            }

            return output;
        }
        catch (Exception ex)
        {
            Faults.Record($"asking {file} what machine this is", ex);

            return null;
        }
    }

    private static string? Ask(string command)
    {
        try
        {
            using var powershell = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (powershell is null)
            {
                return null;
            }

            var answer = powershell.StandardOutput.ReadToEnd();

            // A machine that has lost its WMI service can leave this hanging.
            // Better a fallback identity than an application that never opens.
            if (!powershell.WaitForExit(8000))
            {
                try
                {
                    powershell.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Nothing useful to do; the answer is being discarded anyway.
                }

                return null;
            }

            answer = answer.Trim();

            return answer.Length > 0 && !Meaningless(answer) ? answer : null;
        }
        catch (Exception ex)
        {
            Faults.Record("asking Windows what machine this is", ex);

            return null;
        }
    }

    /// <summary>
    /// The strings manufacturers leave in the field when they have not filled
    /// it in. Every machine of that model would otherwise share one identity,
    /// and one key would licence all of them.
    ///
    /// The all-zeros rule is the one that matters in practice, and it is the
    /// one that nearly went wrong. The machine this was written on reports a
    /// motherboard serial of "00000000" — and the web application rejects that
    /// and falls through to the BIOS, because PHP's in_array compares loosely
    /// and "00000000" equals "0" numerically. An exact match here accepted it,
    /// and this application worked out a different hardware ID from the same
    /// machine — which would have made every licence key a unit already holds
    /// fail on the day they installed the desktop version.
    ///
    /// So it is written as the rule that accident implemented: a serial made
    /// of nothing but zeros and separators is not a serial.
    /// </summary>
    private static bool Meaningless(string value)
    {
        if (value.ToLowerInvariant() is
            "to be filled by o.e.m." or "default string" or "system serial number"
            or "none" or "n/a" or "not applicable" or "not specified")
        {
            return true;
        }

        var bare = value.Where(char.IsLetterOrDigit).ToList();

        return bare.Count == 0 || bare.All(c => c == '0');
    }

    /// <summary>
    /// When the hardware will not say. Weaker — two machines with the same name
    /// would collide — but stable, which is what a licence needs above all: an
    /// identity that changes on its own locks somebody out of their own library.
    /// </summary>
    private static string Fallback()
    {
        Source = "the machine name, because the hardware would not say";

        // Spelt exactly as the web application spells it, including the PHP
        // constant it puts in the middle. This is not decoration: it is what
        // gets hashed, and a different string here is a different machine.
        return $"FALLBACK_{Environment.MachineName}_WINNT";
    }

    private static string Format(string raw)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        var sixteen = hash[..16];

        return string.Join('-',
            sixteen[..4], sixteen[4..8], sixteen[8..12], sixteen[12..]);
    }
}
