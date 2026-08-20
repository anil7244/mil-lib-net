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

    private static string? Serial()
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
