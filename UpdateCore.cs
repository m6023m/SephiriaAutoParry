using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SephiriaAutoParry.Updates
{
    // Fields are populated by DataContractJsonSerializer.
    #pragma warning disable 0649
    [DataContract]
    internal sealed class Release
    {
        [DataMember] public string tag_name;
        [DataMember] public bool draft;
        [DataMember] public bool prerelease;
    }

    [DataContract]
    internal sealed class Manifest
    {
        [DataMember] public int schema;
        [DataMember] public string version;
        [DataMember] public string filename;
        [DataMember] public long size;
        [DataMember] public string sha256;
    }

    internal static class UpdateCore
    {
        public const string Repository = "m6023m/SephiriaAutoParry";
        public const string FileName = "SephiriaAutoParry.dll";
        public const string ReleasePage = "https://github.com/" + Repository + "/releases";
        private const int MaxDllBytes = 16 * 1024 * 1024;

        public static Version ParseVersion(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
                throw new InvalidDataException("Invalid stable release version.");
            return new Version(value + ".0");
        }

        public static T ReadJson<T>(Stream stream)
        {
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        public static void WriteManifest(string path, Manifest manifest)
        {
            using (var stream = File.Create(path))
                new DataContractJsonSerializer(typeof(Manifest)).WriteObject(stream, manifest);
        }

        public static void ValidateManifest(Manifest manifest)
        {
            if (manifest == null || manifest.schema != 1 || manifest.filename != FileName ||
                manifest.size <= 0 || manifest.size > MaxDllBytes || manifest.sha256 == null ||
                !Regex.IsMatch(manifest.sha256, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Invalid update manifest or unsupported updater protocol.");
            ParseVersion(manifest.version);
        }

        public static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static void ValidateFile(string path, Manifest manifest)
        {
            ValidateManifest(manifest);
            if (new FileInfo(path).Length != manifest.size ||
                !String.Equals(Hash(path), manifest.sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded file verification failed.");
            var assembly = AssemblyName.GetAssemblyName(path);
            if (assembly.Name != "SephiriaAutoParry" || assembly.Version != ParseVersion(manifest.version))
                throw new InvalidDataException("Downloaded assembly identity does not match release.");
        }

        private static void Download(string url, Stream destination, long limit)
        {
            // Set TLS before creating the request/service point (required by older .NET defaults).
            // Never disable certificate validation.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "SephiriaAutoParry-Updater/1";
            request.Accept = "application/vnd.github+json";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            request.ServicePoint.Expect100Continue = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            {
                if (response.ResponseUri.Scheme != Uri.UriSchemeHttps || response.ContentLength > limit)
                    throw new InvalidDataException("Invalid download response.");
                var buffer = new byte[32768];
                long total = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += count;
                    if (total > limit) throw new InvalidDataException("Download exceeds size limit.");
                    destination.Write(buffer, 0, count);
                }
            }
        }

        private static T FetchJson<T>(string url)
        {
            using (var stream = new MemoryStream())
            {
                Download(url, stream, 1024 * 1024);
                stream.Position = 0;
                return ReadJson<T>(stream);
            }
        }

        public static Manifest Check(string currentVersion)
        {
            var release = FetchJson<Release>("https://api.github.com/repos/" + Repository + "/releases/latest");
            if (release.draft || release.prerelease || release.tag_name == null || !release.tag_name.StartsWith("v"))
                throw new InvalidDataException("Not a stable release.");
            string version = release.tag_name.Substring(1);
            if (ParseVersion(version) <= ParseVersion(currentVersion)) return null;
            var manifest = FetchJson<Manifest>(ReleasePage + "/download/" + release.tag_name + "/update.json");
            ValidateManifest(manifest);
            if (manifest.version != version) throw new InvalidDataException("Release version mismatch.");
            return manifest;
        }

        public static void Stage(string cache, Manifest manifest)
        {
            ValidateManifest(manifest);
            Directory.CreateDirectory(cache);
            string temporary = Path.Combine(cache, "download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                string dll = Path.Combine(temporary, FileName);
                using (var stream = File.Create(dll))
                    Download(ReleasePage + "/download/v" + manifest.version + "/" + FileName, stream, manifest.size);
                CommitStage(cache, temporary, manifest);
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }

        // All files are validated before the pending directory becomes visible to the preloader.
        internal static void CommitStage(string cache, string temporary, Manifest manifest)
        {
            ValidateFile(Path.Combine(temporary, FileName), manifest);
            WriteManifest(Path.Combine(temporary, "update.json"), manifest);
            string pending = Path.Combine(cache, "pending");
            if (Directory.Exists(pending)) throw new IOException("An update is already waiting for restart.");
            Directory.Move(temporary, pending);
        }

        public static bool HasPending(string cache) { return Directory.Exists(Path.Combine(cache, "pending")); }

        // Called only by the preloader, before BepInEx loads the plugin assembly.
        public static string ApplyPending(string cache, string pluginDirectory)
        {
            string pending = Path.Combine(cache, "pending");
            if (!Directory.Exists(pending)) return null;
            string target = Path.Combine(pluginDirectory, FileName);
            Manifest manifest;
            using (var input = File.OpenRead(Path.Combine(pending, "update.json"))) manifest = ReadJson<Manifest>(input);
            string source = Path.Combine(pending, FileName);
            ValidateFile(source, manifest);
            var installed = AssemblyName.GetAssemblyName(target);
            if (installed.Name != "SephiriaAutoParry" || installed.Version > ParseVersion(manifest.version))
                throw new InvalidDataException("Update would replace a newer or different plugin.");
            if (installed.Version == ParseVersion(manifest.version))
            {
                Directory.Move(pending, Path.Combine(cache, "obsolete-" + Guid.NewGuid().ToString("N")));
                return "Already installed: " + manifest.version;
            }
            string backup = Path.Combine(cache, "previous-" + installed.Version + "-" + Guid.NewGuid().ToString("N") + ".dll");
            // Same-volume atomic replacement; failure leaves the existing target in place.
            File.Replace(source, target, backup);
            try { Directory.Move(pending, Path.Combine(cache, "applied-" + Guid.NewGuid().ToString("N"))); }
            catch (IOException) { /* Replacement succeeded. Next boot recognizes the equal version. */ }
            return "Installed " + manifest.version + "; previous version backed up: " + backup;
        }
    }
}
