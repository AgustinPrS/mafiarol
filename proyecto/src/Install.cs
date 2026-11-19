using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

namespace EmpireBay
{
    public sealed class Install
    {
        public readonly string Root;
        public string Entry { get { return Path.Combine(Root, Distribution.Launcher); } }
        public string Versions { get { return Path.Combine(Root, "versions"); } }
        public static string Self { get { return Assembly.GetExecutingAssembly().Location; } }
        public static Version Current { get { return Inspect(Self); } }
        public static string LocalName(string name)
        {
            return @"Local\EmpireBayRP." + WindowsIdentity.GetCurrent().User.Value + "." + name;
        }

        public Install(string root) { Root = root; }

        public string FileFor(Version version)
        {
            return Path.Combine(Versions, version.ToString(3), Distribution.Launcher);
        }

        public static Version Inspect(string exe)
        {
            var assembly = AssemblyName.GetAssemblyName(exe);
            if (assembly.Name != "MafiaRolLauncher" || assembly.Version.Revision != 0)
                throw new InvalidDataException("El archivo no corresponde al launcher de Empire Bay.");
            var v = assembly.Version;
            return new Version(v.Major, v.Minor, v.Build);
        }

        public static bool Same(string a, string b)
        {
            return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }

        public string Stage()
        {
            string dir = Path.Combine(Root, "temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void Discard(string generatedDirectory)
        {
            try { if (Directory.Exists(generatedDirectory)) Directory.Delete(generatedDirectory, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public static bool Wait(Mutex mutex, int milliseconds)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        static void AtomicCopy(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temp);
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public string Commit(string source, Version expected)
        {
            if (Inspect(source) != expected)
                throw new InvalidDataException("La versión interna no coincide con la etiqueta de GitHub.");
            using (var mutex = new Mutex(false, LocalName("install")))
            {
                if (!Wait(mutex, 15000)) throw new IOException("Otra instalación está en curso.");
                try
                {
                    string dest = FileFor(expected);
                    bool installed = false;
                    if (File.Exists(dest))
                    {
                        try { installed = Inspect(dest) == expected; }
                        catch (BadImageFormatException) { } catch (FileLoadException) { }
                    }
                    if (!installed) AtomicCopy(source, dest);
                    if (!File.Exists(Entry)) AtomicCopy(dest, Entry);
                    return dest;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        public string LatestLocal()
        {
            if (Directory.Exists(Versions))
            {
                var versions = Directory.GetDirectories(Versions).Select(Path.GetFileName)
                    .Select(s => { Version v; return Version.TryParse(s, out v) && v.Build >= 0 && v.Revision < 0 ? v : null; })
                    .Where(v => v != null).OrderByDescending(v => v);
                foreach (Version version in versions)
                {
                    string candidate = FileFor(version);
                    try { if (File.Exists(candidate) && Inspect(candidate) == version) return candidate; }
                    catch (BadImageFormatException) { } catch (IOException) { }
                }
            }
            return Commit(Self, Current);
        }

        public void Shortcut()
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (String.IsNullOrEmpty(folder)) return;
            string path = Path.Combine(folder, "Empire Bay Roleplay.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = Entry;
            shortcut.WorkingDirectory = Root;
            shortcut.Description = "Launcher de Empire Bay Roleplay";
            shortcut.IconLocation = Entry + ",0";
            shortcut.Save();
        }

        public static Process Start(string path, string args)
        {
            return Process.Start(new ProcessStartInfo(path, args) {
                WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = false
            });
        }

        public static void VerifyBuild(string exe, Version version)
        {
            if (Inspect(exe) != version) throw new InvalidDataException("La versión del archivo y la release no coinciden.");
            using (var probe = Start(exe, "--comprobar"))
            {
                if (!probe.WaitForExit(20000))
                {
                    probe.Kill();
                    throw new IOException("La nueva versión no respondió a la prueba de arranque.");
                }
                if (probe.ExitCode != 0) throw new IOException("La nueva versión no superó la prueba de arranque.");
            }
        }
    }
}
