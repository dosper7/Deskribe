using System.Diagnostics;
using System.Text.Json;
using Deskribe.Plugins.Provisioner.Terraform;
using Deskribe.Sdk;
using static Deskribe.Plugins.Tests.T;

namespace Deskribe.Plugins.Tests;

public sealed class TerraformConcurrencyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deskribe-tf-conc-").FullName;
    private readonly TerraformProvisioner _provisioner = new();

    public void Dispose() => Directory.Delete(_root, true);

    // The fake keeps the "selected workspace" in <module>/env (like .terraform/environment) and logs what each command saw.
    private string Fake(string name, string? failVerb = null, string sleep = "0.15")
    {
        var script = Path.Combine(_root, name + "-terraform");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            L="{{_root}}/{{name}}.log"
            ENVF="$PWD/env"
            APP=""
            for a in "$@"; do case "$a" in -var-file=*) APP=$(sed -n 's/.*"app_name": "\(.*\)".*/\1/p' "${a#-var-file=}");; esac; done
            echo "start $1 app=$APP" >> "$L"
            sleep {{sleep}}
            if [ "$1" = "workspace" ]; then echo "$4" > "$ENVF"; echo "select $4" >> "$L"; fi
            if [ "$1" = "apply" ] || [ "$1" = "destroy" ] || [ "$1" = "output" ]; then echo "ran $1 app=$APP ws=$(cat "$ENVF")" >> "$L"; fi
            echo "end $1" >> "$L"
            if [ "$1" = "{{failVerb}}" ]; then echo boom >&2; exit 3; fi
            if [ "$1" = "output" ]; then echo '{}'; fi
            exit 0
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private string Module(string name = "module") => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static ProvisionContext For(string app, string work, string binary, object? extra = null)
    {
        var cfg = new Dictionary<string, object?> { ["workingDir"] = work, ["binary"] = binary };
        if (extra is not null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject()) cfg[p.Name] = p.Value;
        return Ctx([], cfg) with { AppName = app };
    }

    private string[] Log(string name) => File.ReadAllLines(Path.Combine(_root, name + ".log"));

    [Fact]
    public async Task ConcurrentOperationsOnOneDirectory_NeverInterleave_AndSeeTheirOwnWorkspace()
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Fake("a");
        var work = Module();

        var tasks = new List<Task<ProvisionResult>>();
        foreach (var app in new[] { "one", "two", "three" })
        {
            tasks.Add(Task.Run(() => _provisioner.ApplyAsync(For(app, work, bin))));
            tasks.Add(Task.Run(() => _provisioner.DestroyAsync(For(app + "x", work, bin))));
        }
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Success, string.Join(";", r.Errors)));

        var log = Log("a");
        // Every start is followed directly by its own end: no two commands overlap.
        for (var i = 0; i < log.Length; i++)
        {
            if (!log[i].StartsWith("start ")) continue;
            var verb = log[i].Split(' ')[1];
            Assert.Contains("end " + verb, log[i + 1] + "|" + log[i + 2]);
        }
        // Each apply/destroy/output ran in the workspace selected for it, with no other workspace selected in between.
        var selected = "";
        var ran = 0;
        foreach (var line in log)
        {
            if (line.StartsWith("select ")) selected = line["select ".Length..];
            if (!line.StartsWith("ran ")) continue;
            ran++;
            var ws = line[(line.IndexOf("ws=", StringComparison.Ordinal) + 3)..];
            Assert.Equal(selected, ws);
            if (!line.StartsWith("ran output"))
                Assert.Equal(line.Split("app=")[1].Split(' ')[0] + "-dev", ws);
        }
        Assert.Equal(9, ran);
        // Operations are contiguous: init, select, verb, (output) per operation, never mixed.
        var sequence = log.Where(l => l.StartsWith("start ")).Select(l => l.Split(' ')[1]).ToList();
        for (var i = 0; i < sequence.Count;)
        {
            Assert.Equal(["init", "workspace"], sequence.Skip(i).Take(2));
            var verb = sequence[i + 2];
            i += 3;
            if (verb == "apply") { Assert.Equal("output", sequence[i]); i++; }
            else Assert.Equal("destroy", verb);
        }
    }

    [Fact]
    public async Task DifferentDirectories_DoNotBlockEachOther()
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Fake("b");
        var one = Module("m1");
        var two = Module("m2");
        // Hold m1 from "another process"; m2 must still proceed immediately.
        using var held = new FileStream(Path.Combine(one, TerraformDirectoryLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = await _provisioner.DestroyAsync(For("app", two, bin, new { lockTimeoutSeconds = 0 }));
        Assert.True(result.Success, string.Join(";", result.Errors));
    }

    [Fact]
    public async Task LockHeldElsewhere_TimesOutWithClearError_ThenSucceedsAfterRelease()
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Fake("c");
        var work = Module();
        var lockPath = Path.Combine(work, TerraformDirectoryLock.LockFileName);
        var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var blocked = await _provisioner.DestroyAsync(For("app", work, bin, new { lockTimeoutSeconds = 0.3 }));
            Assert.False(blocked.Success);
            Assert.Contains(blocked.Errors, e => e.Contains("Timed out") && e.Contains("lockTimeoutSeconds"));
            Assert.False(File.Exists(Path.Combine(_root, "c.log")));
        }
        finally { held.Dispose(); }

        var ok = await _provisioner.DestroyAsync(For("app", work, bin, new { lockTimeoutSeconds = 5 }));
        Assert.True(ok.Success, string.Join(";", ok.Errors));
    }

    [Fact]
    public async Task LockHeldByAnotherProcess_BlocksUntilThatProcessExits_EvenWhenKilled()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/python3")) return;
        var bin = Fake("d");
        var work = Module();
        var lockPath = Path.Combine(work, TerraformDirectoryLock.LockFileName);
        var psi = new ProcessStartInfo("/usr/bin/python3")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import fcntl,sys,time\nf=open(sys.argv[1],'a')\nfcntl.flock(f,fcntl.LOCK_EX)\nprint('locked',flush=True)\ntime.sleep(600)");
        psi.ArgumentList.Add(lockPath);
        using var holder = Process.Start(psi)!;
        try
        {
            Assert.Equal("locked", await holder.StandardOutput.ReadLineAsync());

            var blocked = await _provisioner.DestroyAsync(For("app", work, bin, new { lockTimeoutSeconds = 0.3 }));
            Assert.False(blocked.Success);

            var waiting = _provisioner.DestroyAsync(For("app", work, bin, new { lockTimeoutSeconds = 60 }));
            await Task.Delay(300);
            Assert.False(waiting.IsCompleted);

            holder.Kill();
            await holder.WaitForExitAsync();
            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(result.Success, string.Join(";", result.Errors));
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
        }
    }

    [Fact]
    public async Task CancellationWhileWaiting_ThrowsPromptly_AndReleasesTheQueue()
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Fake("e");
        var work = Module();
        var held = new FileStream(Path.Combine(work, TerraformDirectoryLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var waiting = _provisioner.ApplyAsync(For("app", work, bin), cts.Token);
            await Task.Delay(300);
            Assert.False(waiting.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally { held.Dispose(); }

        // The cancelled waiter must not have leaked the in-process gate or the file lock.
        var ok = await _provisioner.DestroyAsync(For("app", work, bin, new { lockTimeoutSeconds = 5 }));
        Assert.True(ok.Success, string.Join(";", ok.Errors));
    }

    [Fact]
    public async Task CancellationWhileWaitingOnInProcessOperation_Throws()
    {
        if (OperatingSystem.IsWindows()) return;
        var slow = Fake("f", sleep: "1");
        var work = Module();
        var first = _provisioner.DestroyAsync(For("a", work, slow));
        await Task.Delay(300);
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provisioner.DestroyAsync(For("b", work, slow), cts.Token));
        Assert.True((await first).Success);
    }

    [Fact]
    public async Task LockReleasedAfterFailingCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        var failing = Fake("g", failVerb: "apply");
        var work = Module();
        var failed = await _provisioner.ApplyAsync(For("a", work, failing));
        Assert.False(failed.Success);

        var ok = await _provisioner.DestroyAsync(For("b", work, failing, new { lockTimeoutSeconds = 5 }));
        Assert.True(ok.Success, string.Join(";", ok.Errors));
        using var probe = new FileStream(Path.Combine(work, TerraformDirectoryLock.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task PathSpellingsOfTheSameDirectory_ShareOneLock()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = Module("m");
        var sep = Path.DirectorySeparatorChar;
        var spellings = new[]
        {
            work, work + sep, Path.Combine(work, ".") , Path.Combine(_root, "other", "..", "m"),
            Path.GetRelativePath(Directory.GetCurrentDirectory(), work)
        };
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        Assert.Single(spellings.Select(TerraformDirectoryLock.Canonicalize).Distinct());

        using var first = await TerraformDirectoryLock.AcquireAsync(spellings[0], TimeSpan.FromSeconds(5), default);
        Assert.NotNull(first);
        foreach (var s in spellings.Skip(1))
            Assert.Null(await TerraformDirectoryLock.AcquireAsync(s, TimeSpan.FromMilliseconds(150), default));
        // A different directory is independent.
        using var other = await TerraformDirectoryLock.AcquireAsync(Path.Combine(_root, "other"), TimeSpan.Zero, default);
        Assert.NotNull(other);
    }
}
