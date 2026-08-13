using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HistoryDeleter
{
    /// <summary>
    /// Locates the installed Geo SCADA client and makes its assemblies loadable from this process.
    /// The tool deliberately does not ship copies of the Schneider DLLs; it binds to whatever
    /// version is installed on the machine.
    /// </summary>
    internal static class GeoScadaClient
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static string _installDir;

        internal static string InstallDirectory { get { return _installDir; } }

        /// <summary>True when running as a 64-bit process, which dictates which install tree we need.</summary>
        private static bool Is64BitProcess { get { return IntPtr.Size == 8; } }

        internal static bool Initialise(out string error)
        {
            error = null;
            _installDir = Locate();

            if (_installDir == null)
            {
                error = BuildNotFoundMessage();
                return false;
            }

            // Lets the CLR find the native DBClient.dll that ClearScada.Client P/Invokes.
            SetDllDirectory(_installDir);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromInstallDirectory;
            return true;
        }

        private static Assembly ResolveFromInstallDirectory(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(_installDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static string Locate()
        {
            foreach (string candidate in CandidateDirectories())
            {
                if (candidate == null) continue;
                string dir = candidate.TrimEnd('\\', '/');
                if (IsUsable(dir)) return dir;
            }
            return null;
        }

        private static IEnumerable<string> CandidateDirectories()
        {
            // The installer records both trees under the same 64-bit registry key. Pick the one
            // matching this process, because DBClient.dll is native and cannot be loaded cross-bitness.
            string valueName = Is64BitProcess ? "InstallLocation" : "InstallLocationx86";

            yield return ReadRegistry(RegistryView.Registry64, valueName);
            yield return ReadRegistry(RegistryView.Registry32, valueName);

            string programFiles = Environment.GetEnvironmentVariable(
                Is64BitProcess ? "ProgramW6432" : "ProgramFiles(x86)");
            if (string.IsNullOrEmpty(programFiles))
                programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            yield return Path.Combine(programFiles, @"Schneider Electric\ClearSCADA");
            yield return Path.Combine(programFiles, @"AVEVA\Geo SCADA Expert");
            yield return Path.Combine(programFiles, @"Schneider Electric\Geo SCADA Expert");
        }

        private static string ReadRegistry(RegistryView view, string valueName)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\Schneider Electric\ClearSCADA"))
                {
                    if (key == null) return null;
                    return key.GetValue(valueName) as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUsable(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (!File.Exists(Path.Combine(dir, "ClearScada.Client.dll"))) return false;
            if (!File.Exists(Path.Combine(dir, "DBClient.dll"))) return false;
            return true;
        }

        private static string BuildNotFoundMessage()
        {
            string bitness = Is64BitProcess ? "64-bit" : "32-bit";
            string other = Is64BitProcess ? "HistoryDeleter32.exe" : "HistoryDeleter.exe";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("This tool needs the Geo SCADA (ClearSCADA) client software installed, but no ");
            sb.AppendLine(bitness + " installation could be found.");
            sb.AppendLine();
            sb.AppendLine("Looked for ClearScada.Client.dll and DBClient.dll in:");
            foreach (string candidate in CandidateDirectories())
                if (candidate != null) sb.AppendLine("    " + candidate.TrimEnd('\\', '/'));
            sb.AppendLine();
            sb.AppendLine("If the client is installed but with the other bitness, run " + other + " instead.");
            return sb.ToString();
        }
    }
}
