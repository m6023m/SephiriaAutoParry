using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using SephiriaAutoParry.Updates;

[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.1.0.0")]

namespace SephiriaAutoParry.Bootstrap
{
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs { get { return new string[0]; } }
        public static void Patch(AssemblyDefinition assembly) { }
        public static void Initialize()
        {
            var log = Logger.CreateLogSource("AutoParry Updater");
            string cache = Path.Combine(Paths.CachePath, "SephiriaAutoParry");
            try
            {
                var result = UpdateCore.ApplyPending(cache, Paths.PluginPath);
                log.LogInfo(result ?? "No staged update; current plugin preserved.");
                if (result != null) File.WriteAllText(Path.Combine(cache, "last-result.txt"), result);
            }
            catch (Exception exception)
            {
                log.LogError("Update not applied; current plugin preserved: " + exception);
                try
                {
                    Directory.CreateDirectory(cache);
                    File.WriteAllText(Path.Combine(cache, "failed.txt"), exception.Message);
                    string pending = Path.Combine(cache, "pending");
                    if (Directory.Exists(pending))
                        Directory.Move(pending, Path.Combine(cache, "failed-" + Guid.NewGuid().ToString("N")));
                }
                catch (Exception statusError) { log.LogWarning(statusError.Message); }
            }
        }
    }
}
