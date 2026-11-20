using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace LauncherCleaner;

[SupportedOSPlatform("windows")]
internal static class Program
{
    // Ключові слова (нормалізовані: без пробілів, тире, крапок, у нижньому регістрі)
    static readonly List<string> Keywords = new() { "tlauncher", "horizonlauncher" };

    static readonly HashSet<string> SkipAtDriveRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent",
        "Users", "ProgramData", "Program Files", "Program Files (x86)", "PerfLogs"
    };

    static readonly HashSet<string> SkipInMinecraft = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "assets", "libraries", "mods", "resourcepacks", "shaderpacks", "screenshots"
    };

    sealed record RegItem(RegistryHive Hive, RegistryView View, string KeyPath, string? ValueName, string Label);

    static bool Silent;

    static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    static bool IsMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var n = Norm(s);
        foreach (var k in Keywords)
            if (n.Contains(k)) return true;
        return false;
    }

    static bool IsRealDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            return !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Ця програма працює лише на Windows.");
            return 1;
        }

        Silent = args.Any(a => a is "-y" or "--yes" or "/y");
        bool wipeMinecraft = args.Any(a => a == "--wipe-minecraft");
        foreach (var a in args.Where(a => a.StartsWith("--name=", StringComparison.OrdinalIgnoreCase)))
        {
            var extra = Norm(a.Substring(7));
            if (extra.Length >= 4 && !Keywords.Contains(extra)) Keywords.Add(extra);
        }

        Console.WriteLine("=== Launcher Cleaner: TLauncher + Horizon Launcher ===");
        Console.WriteLine("Сканую диски та реєстр, зачекай...\n");

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanFileSystem(found);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dotMinecraft = Path.Combine(appData, ".minecraft");
        if (wipeMinecraft && Directory.Exists(dotMinecraft)) found.Add(dotMinecraft);

        var targets = RemoveNested(found);
        var regItems = ScanRegistry();

        if (targets.Count == 0 && regItems.Count == 0)
        {
            Console.WriteLine("Нічого не знайдено — слідів TLauncher / Horizon Launcher немає.");
            Pause();
            return 0;
        }

        Console.WriteLine("Знайдено файли та папки:");
        foreach (var t in targets)
            Console.WriteLine($"  [{(Directory.Exists(t) ? "ПАПКА" : "ФАЙЛ ")}] {t}");

        if (regItems.Count > 0)
        {
            Console.WriteLine("\nЗнайдено записи реєстру:");
            foreach (var r in regItems)
                Console.WriteLine($"  [REG] {r.Label}");
        }

        if (wipeMinecraft)
        {
            Console.WriteLine($"\n!!! УВАГА: буде видалено ПОВНІСТЮ {dotMinecraft} (моди, світи, версії, налаштування) !!!");
        }

        Console.WriteLine();
        if (!Silent)
        {
            Console.Write("Видалити все це НАЗАВЖДИ? (y/n): ");
            var ans = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (ans is not ("y" or "yes" or "н" or "так"))
            {
                Console.WriteLine("Скасовано. Нічого не видалено.");
                Pause();
                return 0;
            }
        }

        KillProcesses();

        int ok = 0;
        var failed = new List<string>();

        foreach (var t in targets)
        {
            if (!IsSafeTarget(t, wipeMinecraft ? dotMinecraft : null))
            {
                failed.Add(t + " (пропущено: небезпечний шлях)");
                continue;
            }

            if (DeletePath(t)) { ok++; Console.WriteLine($"  видалено: {t}"); }
            else failed.Add(t);
        }

        foreach (var r in regItems)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(r.Hive, r.View);
                if (r.ValueName == null)
                {
                    bk.DeleteSubKeyTree(r.KeyPath, false);
                }
                else
                {
                    using var k = bk.OpenSubKey(r.KeyPath, true);
                    k?.DeleteValue(r.ValueName, false);
                }
                ok++;
                Console.WriteLine($"  видалено: [REG] {r.Label}");
            }
            catch
            {
                failed.Add("[REG] " + r.Label);
            }
        }

        Console.WriteLine($"\nГотово. Видалено об'єктів: {ok}.");
        if (failed.Count > 0)
        {
            Console.WriteLine($"Не вдалося видалити ({failed.Count}):");
            foreach (var f in failed) Console.WriteLine("  " + f);
            Console.WriteLine("\nЗакрий усі програми, перезавантаж ПК і запусти ще раз (від імені адміністратора).");
        }

        Pause();
        return failed.Count == 0 ? 0 : 2;
    }

    static void Pause()
    {
        if (Silent) return;
        Console.WriteLine("\nНатисни Enter, щоб закрити...");
        Console.ReadLine();
    }

    // ---------- Пошук файлів ----------

    static void ScanFileSystem(HashSet<string> found)
    {
        var roots = new List<(string Path, int Depth)>();

        void Add(string? p, int depth)
        {
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add((p, depth));
        }

        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var appData = F(Environment.SpecialFolder.ApplicationData);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);

        Add(appData, 1);
        Add(local, 1);
        Add(Path.Combine(local, "Programs"), 1);
        Add(Path.Combine(local, "Temp"), 1);
        Add(Path.GetTempPath(), 1);
        Add(F(Environment.SpecialFolder.CommonApplicationData), 1);
        Add(profile, 1);
        Add(F(Environment.SpecialFolder.Desktop), 2);
        Add(F(Environment.SpecialFolder.CommonDesktopDirectory), 1);
        Add(Path.Combine(profile, "Downloads"), 2);
        Add(F(Environment.SpecialFolder.MyDocuments), 2);
        Add(F(Environment.SpecialFolder.Programs), 2);
        Add(F(Environment.SpecialFolder.CommonPrograms), 2);
        Add(F(Environment.SpecialFolder.ProgramFiles), 1);
        Add(F(Environment.SpecialFolder.ProgramFilesX86), 1);

        // .minecraft: шукаємо файли TLauncher всередині (profiles json тощо)
        Add(Path.Combine(appData, ".minecraft"), 2);

        // Усі локальні диски: корінь + 2 рівні вглиб (наприклад D:\Games\TLauncher)
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady)
                        Add(d.RootDirectory.FullName, 3);
                }
                catch { }
            }
        }
        catch { }

        foreach (var (path, depth) in roots)
            Walk(path, depth, found, isDriveRoot: Path.GetPathRoot(path) == path);

        // Евристика для Horizon Launcher: папка "Horizon" у AppData, всередині якої є щось із "launcher"
        foreach (var baseDir in new[] { appData, local })
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    if (Norm(Path.GetFileName(dir)) != "horizon") continue;
                    bool hasLauncher = Directory.EnumerateFileSystemEntries(dir)
                        .Any(e => Norm(Path.GetFileName(e)).Contains("launcher"));
                    if (hasLauncher) found.Add(dir);
                }
            }
            catch { }
        }
    }

    static void Walk(string dir, int depth, HashSet<string> found, bool isDriveRoot = false)
    {
        List<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch { return; }

        foreach (var e in entries)
        {
            var name = Path.GetFileName(e);
            if (string.IsNullOrEmpty(name)) continue;

            if (IsMatch(name))
            {
                found.Add(e);
                continue;
            }

            if (depth <= 1 || !IsRealDir(e)) continue;
            if (isDriveRoot && SkipAtDriveRoot.Contains(name)) continue;
            if (SkipInMinecraft.Contains(name) && dir.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase)) continue;

            Walk(e, depth - 1, found);
        }
    }

    static List<string> RemoveNested(HashSet<string> found)
    {
        var sorted = found.OrderBy(p => p.Length).ToList();
        var result = new List<string>();
        foreach (var p in sorted)
        {
            bool nested = result.Any(r =>
                p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!nested) result.Add(p);
        }
        return result.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsSafeTarget(string path, string? allowedMinecraft)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Length <= 3) return false;
            if (Path.GetPathRoot(full)?.TrimEnd('\\') == full) return false;

            if (allowedMinecraft != null &&
                string.Equals(full, Path.GetFullPath(allowedMinecraft).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;

            var name = Path.GetFileName(full);
            if (IsMatch(name)) return true;

            // евристична папка "Horizon"
            return Norm(name) == "horizon";
        }
        catch { return false; }
    }

    // ---------- Реєстр ----------

    static List<RegItem> ScanRegistry()
    {
        var items = new List<RegItem>();
        var hives = new (RegistryHive Hive, RegistryView[] Views)[]
        {
            (RegistryHive.CurrentUser, new[] { RegistryView.Default }),
            (RegistryHive.LocalMachine, new[] { RegistryView.Registry64, RegistryView.Registry32 })
        };

        foreach (var (hive, views) in hives)
        {
            foreach (var view in views)
            {
                try
                {
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    ScanUninstall(bk, hive, view, items);
                    ScanRun(bk, hive, view, items);
                    ScanSoftware(bk, hive, view, items);
                }
                catch { }
            }
        }
        return items;
    }

    static string HiveName(RegistryHive h) => h == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    static void ScanUninstall(RegistryKey bk, RegistryHive hive, RegistryView view, List<RegItem> items)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using var k = bk.OpenSubKey(path);
        if (k == null) return;

        foreach (var name in k.GetSubKeyNames())
        {
            try
            {
                using var sk = k.OpenSubKey(name);
                bool match = IsMatch(name);
                if (!match && sk != null)
                {
                    foreach (var v in new[] { "DisplayName", "InstallLocation", "UninstallString", "DisplayIcon" })
                    {
                        if (sk.GetValue(v) is string s && IsMatch(s)) { match = true; break; }
                    }
                }
                if (match)
                    items.Add(new RegItem(hive, view, path + "\\" + name, null, $"{HiveName(hive)}\\{path}\\{name}"));
            }
            catch { }
        }
    }

    static void ScanRun(RegistryKey bk, RegistryHive hive, RegistryView view, List<RegItem> items)
    {
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using var k = bk.OpenSubKey(path);
        if (k == null) return;

        foreach (var valueName in k.GetValueNames())
        {
            try
            {
                var data = k.GetValue(valueName) as string;
                if (IsMatch(valueName) || IsMatch(data))
                    items.Add(new RegItem(hive, view, path, valueName, $"{HiveName(hive)}\\{path} -> {valueName}"));
            }
            catch { }
        }
    }

    static void ScanSoftware(RegistryKey bk, RegistryHive hive, RegistryView view, List<RegItem> items)
    {
        using var k = bk.OpenSubKey("Software");
        if (k == null) return;

        foreach (var name in k.GetSubKeyNames())
        {
            if (IsMatch(name))
                items.Add(new RegItem(hive, view, "Software\\" + name, null, $"{HiveName(hive)}\\Software\\{name}"));
        }
    }

    // ---------- Видалення ----------

    static void KillProcesses()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != Environment.ProcessId && IsMatch(p.ProcessName))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    Console.WriteLine($"  закрито процес: {p.ProcessName}");
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    static bool DeletePath(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return true;
                }

                if (Directory.Exists(path))
                {
                    var attr = File.GetAttributes(path);
                    if (attr.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // посилання / junction: видаляємо лише саме посилання, не вміст
                        Directory.Delete(path, false);
                        return true;
                    }

                    ClearAttributes(path);
                    Directory.Delete(path, true);
                    return true;
                }

                return true;
            }
            catch
            {
                Thread.Sleep(600);
            }
        }

        return !File.Exists(path) && !Directory.Exists(path);
    }

    static void ClearAttributes(string dir)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint // Hidden/System теж обробляємо
        };

        try
        {
            foreach (var e in Directory.EnumerateFileSystemEntries(dir, "*", opts))
            {
                try { File.SetAttributes(e, FileAttributes.Normal); } catch { }
            }
            File.SetAttributes(dir, FileAttributes.Normal);
        }
        catch { }
    }
}
