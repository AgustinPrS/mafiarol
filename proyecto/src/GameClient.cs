using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmpireBay
{
    public sealed class GameClient
    {
        readonly Install install;
        readonly Distribution downloads;

        public GameClient(Install install, Distribution downloads)
        {
            this.install = install;
            this.downloads = downloads;
        }

        string Package(Settings s) { return Path.Combine(install.Root, "cliente", s.clientSha256.ToLowerInvariant()); }

        public async Task<string> Setup(Settings s, Action<int> progress, Action<string> status, CancellationToken token)
        {
            string stage = install.Stage();
            try
            {
                string package = Path.Combine(stage, "Cliente." + s.clientType);
                await downloads.Fetch(s.clientUrl, s.clientSha256, package, Distribution.ClientLimit, -1, false, progress, token);
                token.ThrowIfCancellationRequested();
                status("Instalando el cliente…");
                if (s.clientType == "zip")
                {
                    string dest = Package(s);
                    if (!Directory.Exists(dest))
                    {
                        string extracted = Path.Combine(stage, "extraido");
                        await Task.Run(() => Extract(package, extracted, token), token);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        Directory.Move(extracted, dest);
                    }
                    return "Cliente instalado en " + dest;
                }
                var start = s.clientType == "msi"
                    ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/i \"" + package + "\"")
                    : new ProcessStartInfo(package);
                start.UseShellExecute = true;
                start.WorkingDirectory = stage;
                using (Process process = Process.Start(start))
                {
                    if (process == null) throw new IOException("Windows no pudo abrir el instalador.");
                    await Task.Run(() => process.WaitForExit());
                    int code = process.ExitCode;
                    if (code == 1602) return "Instalación cancelada.";
                    if (code == 3010 || code == 1641) return "Cliente instalado. Windows requiere reiniciar.";
                    if (code != 0) throw new IOException("El instalador finalizó con el código " + code + ".");
                }
                return "El instalador del cliente finalizó correctamente.";
            }
            finally { Install.Discard(stage); }
        }

        static void Extract(string zip, string output, CancellationToken token)
        {
            Directory.CreateDirectory(output);
            string prefix = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > 100000)
                    throw new InvalidDataException("El paquete ZIP está vacío o es demasiado grande.");
                long bytes = 0;
                int files = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName.Replace('/', '\\');
                    bool directory = name.EndsWith("\\", StringComparison.Ordinal);
                    string[] parts = name.TrimEnd('\\').Split('\\');
                    if (parts.Any(p => String.IsNullOrWhiteSpace(p) || p.EndsWith(".") || p.EndsWith(" ") ||
                        p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                        Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase)))
                        throw new InvalidDataException("El ZIP contiene una ruta no válida.");
                    string path = Path.GetFullPath(Path.Combine(output, name));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta no válida.");
                    if (directory) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    files++;
                    using (var source = entry.Open())
                    using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        int n;
                        byte[] buffer = new byte[81920];
                        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            bytes += n;
                            if (bytes > 8L * 1024 * 1024 * 1024) throw new IOException("El ZIP supera 8 GiB descomprimido.");
                            target.Write(buffer, 0, n);
                        }
                    }
                }
                if (files == 0) throw new InvalidDataException("El paquete ZIP no contiene archivos.");
            }
        }

        public void Play(Settings s, System.Windows.Window owner)
        {
            if (!s.playEnabled) return;
            string remember = Path.Combine(install.Root, "cliente-ruta.txt");
            string exe = File.Exists(remember) ? File.ReadAllText(remember) : null;
            if (!Matches(exe, s.clientExecutable) && s.HasClient && s.clientType == "zip" && Directory.Exists(Package(s)))
                exe = Directory.EnumerateFiles(Package(s), s.clientExecutable, SearchOption.AllDirectories).FirstOrDefault();
            if (!Matches(exe, s.clientExecutable))
            {
                var choose = new OpenFileDialog {
                    Title = "Seleccioná " + s.clientExecutable, Filter = "Cliente multijugador|" + s.clientExecutable,
                    CheckFileExists = true, Multiselect = false
                };
                if (choose.ShowDialog(owner) != true) return;
                exe = choose.FileName;
                if (!Matches(exe, s.clientExecutable)) throw new InvalidDataException("Seleccioná el ejecutable indicado.");
                File.WriteAllText(remember, exe);
            }
            string args = s.connectArguments.Replace("{host}", s.host).Replace("{port}", s.port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (Install.Start(exe, args)) { }
        }

        static bool Matches(string path, string expected)
        {
            return File.Exists(path) && String.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
