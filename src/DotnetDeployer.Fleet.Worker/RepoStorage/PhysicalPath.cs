using System.Runtime.InteropServices;

namespace DotnetDeployer.Fleet.WorkerService.RepoStorage;

/// <summary>
/// Resolves a directory to its physical path, with no symbolic link in it.
/// </summary>
/// <remarks>
/// The compiler resolves relative source paths against the physical working
/// directory but takes absolute ones as given. Under a symbolic link, a file a
/// project includes both relatively and through <c>$(MSBuildThisFileDirectory)</c>
/// reaches the compiler under two paths, so it is compiled twice (CS0101)
/// instead of once with warning CS2002. Repositories kept under the physical
/// path build the same on every worker.
/// </remarks>
internal static class PhysicalPath
{
    /// <summary>
    /// Returns the physical path of the existing <paramref name="path"/>, or its
    /// full path when it cannot be resolved (it does not exist, or on Windows).
    /// </summary>
    public static string Of(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        var resolved = RealPath(fullPath, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return fullPath;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? fullPath;
        }
        finally
        {
            Free(resolved);
        }
    }

    [DllImport("libc", EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
