namespace RaindropServer.Security;

internal static class FilePermissions
{
    /// <summary>
    /// Takes a file holding a private key down to owner-only. On a shared host the default
    /// permissions would let any local account read it.
    /// </summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
