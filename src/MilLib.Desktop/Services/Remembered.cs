namespace MilLib.Desktop.Services;

/// <summary>
/// The username to fill in next time, and nothing else.
///
/// Deliberately not a remembered session and deliberately not a password. This
/// application signs you out when it closes — the README says so and people
/// rely on it — so a "remember me" that quietly kept somebody signed in would
/// contradict what the product says about itself, on a machine that may sit in
/// a shared office.
///
/// What is left is the useful half: not retyping the same eight characters
/// every morning. It is kept in a small file beside the data, so a folder
/// handed to somebody else does not carry anybody's name into it — and if the
/// file cannot be written, nothing goes wrong at all, it simply is not
/// remembered.
/// </summary>
public static class Remembered
{
    private static string Path => System.IO.Path.Combine(Workspace.Pictures, "signed-in-as.txt");

    public static string Username
    {
        get
        {
            try
            {
                return File.Exists(Path) ? File.ReadAllText(Path).Trim() : "";
            }
            catch (Exception ex)
            {
                Faults.Record("reading the remembered username", ex);

                return "";
            }
        }
    }

    /// <summary>Keep this name, or forget whatever was kept when it is empty.</summary>
    public static void Keep(string username)
    {
        try
        {
            username = username.Trim();

            if (username.Length == 0)
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }

                return;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

            File.WriteAllText(Path, username);
        }
        catch (Exception ex)
        {
            // Worth recording and worth nothing else. Not being able to
            // remember a username is not a reason to interrupt anybody.
            Faults.Record("remembering the username", ex);
        }
    }
}
