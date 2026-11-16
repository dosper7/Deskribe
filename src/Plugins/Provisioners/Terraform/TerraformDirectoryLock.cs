using System.Collections.Concurrent;

namespace Deskribe.Plugins.Provisioner.Terraform;

/// <summary>
/// Serialises Terraform operations per working directory. Terraform keeps the selected workspace in shared state
/// inside the directory, so a whole apply/destroy sequence must run exclusively.
/// Within a process an async semaphore keyed by the canonical directory is used; across processes an OS-level
/// exclusive lock on <see cref="LockFileName"/> inside the directory (FileShare.None: flock on Unix, LockFileEx on
/// Windows). The OS drops the lock when the process dies, so a crash never leaves the directory locked. The file itself
/// is empty and is intentionally never deleted (deleting would race with other waiters); it is safe to ignore in VCS.
/// </summary>
internal static class TerraformDirectoryLock
{
    public const string LockFileName = ".deskribe-terraform.lock";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static string Canonicalize(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0)) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            if (Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target) full = target.FullName;
        }
        catch (IOException) { }
        return full;
    }

    /// <summary>Returns null on timeout. Throws OperationCanceledException when cancelled while waiting.</summary>
    public static async Task<IDisposable?> AcquireAsync(string directory, TimeSpan timeout, CancellationToken ct)
    {
        var canonical = Canonicalize(directory);
        var gate = Gates.GetOrAdd(canonical, _ => new SemaphoreSlim(1, 1));
        var deadline = DateTime.UtcNow + timeout;

        if (!await gate.WaitAsync(timeout, ct)) return null;
        try
        {
            var path = Path.Combine(canonical, LockFileName);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var stream = TryOpen(path);
                if (stream is not null) return new Releaser(gate, stream);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    gate.Release();
                    return null;
                }
                await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct);
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException) when (File.Exists(path))
        {
            return null;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, FileStream stream) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            stream.Dispose();
            gate.Release();
        }
    }
}
