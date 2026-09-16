using System;
using System.IO;
using SephiriaAutoParry.Updates;

class UpdateTests
{
    static int count;
    static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name); count++;
    }
    static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception) { Assert(true, name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    static string NewStage(string cache, string fixture)
    {
        string temporary = Path.Combine(cache, "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        File.Copy(fixture, Path.Combine(temporary, UpdateCore.FileName));
        return temporary;
    }
    static int Main(string[] args)
    {
        string root = args[0], old = args[1], next = args[2];
        string cache = Path.Combine(root, "cache"), plugins = Path.Combine(root, "plugins");
        Directory.CreateDirectory(cache); Directory.CreateDirectory(plugins);
        string target = Path.Combine(plugins, UpdateCore.FileName);
        File.Copy(old, target);
        string oldHash = UpdateCore.Hash(target);
        var manifest = new Manifest { schema = 1, version = "0.1.0", filename = UpdateCore.FileName,
            size = new FileInfo(next).Length, sha256 = UpdateCore.Hash(next) };
        Assert(UpdateCore.ParseVersion("0.10.0") > UpdateCore.ParseVersion("0.9.9"), "numeric version ordering");
        foreach (string invalid in new [] { "0.1", "v0.1.0", "0.1.0-beta", "../0.1.0", "01.1.0", "0.1.0.1" })
            Reject(() => UpdateCore.ParseVersion(invalid), "reject version " + invalid);
        string temporary = NewStage(cache, next);
        string file = Path.Combine(temporary, UpdateCore.FileName);
        manifest.filename = "../SephiriaAutoParry.dll";
        Reject(() => UpdateCore.CommitStage(cache, temporary, manifest), "reject path traversal");
        manifest.filename = UpdateCore.FileName;
        manifest.schema = 2;
        Reject(() => UpdateCore.CommitStage(cache, temporary, manifest), "reject unsupported protocol");
        manifest.schema = 1;
        manifest.size++;
        Reject(() => UpdateCore.CommitStage(cache, temporary, manifest), "reject truncated download");
        manifest.size--;
        manifest.sha256 = new string('0', 64);
        Reject(() => UpdateCore.CommitStage(cache, temporary, manifest), "reject bad hash");
        manifest.sha256 = UpdateCore.Hash(next);
        manifest.version = "0.2.0";
        Reject(() => UpdateCore.CommitStage(cache, temporary, manifest), "reject assembly version mismatch");
        manifest.version = "0.1.0";
        Assert(!UpdateCore.HasPending(cache) && UpdateCore.Hash(target) == oldHash, "failed downloads preserve installed version");
        UpdateCore.CommitStage(cache, temporary, manifest);
        Assert(UpdateCore.HasPending(cache) && UpdateCore.Hash(target) == oldHash, "staging never replaces running plugin");
        Reject(() => UpdateCore.CommitStage(cache, NewStage(cache, next), manifest), "preserve existing valid pending update");
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => UpdateCore.ApplyPending(cache, plugins), "locked target fails safely");
        Assert(UpdateCore.Hash(target) == oldHash && UpdateCore.HasPending(cache), "failure preserves target and pending file");
        string result = UpdateCore.ApplyPending(cache, plugins);
        Assert(UpdateCore.Hash(target) == manifest.sha256 && !UpdateCore.HasPending(cache), "next boot applies verified update");
        string[] backups = Directory.GetFiles(cache, "previous-*.dll");
        Assert(backups.Length == 1 && UpdateCore.Hash(backups[0]) == oldHash, "atomic backup keeps previous version");
        Assert(UpdateCore.ApplyPending(cache, plugins) == null, "repeated boot is harmless");
        UpdateCore.CommitStage(cache, NewStage(cache, next), manifest);
        Assert(UpdateCore.ApplyPending(cache, plugins).StartsWith("Already installed"), "equal version is discarded");
        var older = new Manifest { schema = 1, version = "0.0.0", filename = UpdateCore.FileName,
            size = new FileInfo(old).Length, sha256 = UpdateCore.Hash(old) };
        UpdateCore.CommitStage(cache, NewStage(cache, old), older);
        Reject(() => UpdateCore.ApplyPending(cache, plugins), "reject downgrade");
        Assert(UpdateCore.Hash(target) == manifest.sha256, "downgrade leaves current plugin untouched");
        File.WriteAllText(Path.Combine(cache, "pending", "update.json"), "broken");
        Reject(() => UpdateCore.ApplyPending(cache, plugins), "corrupt manifest fails safely");
        Assert(UpdateCore.Hash(target) == manifest.sha256, "corrupt pending data preserves current version");
        Console.WriteLine(count + " updater checks passed.");
        return 0;
    }
}
