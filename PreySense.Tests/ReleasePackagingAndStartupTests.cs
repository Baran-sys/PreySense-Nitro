using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Xml.Linq;
using PreySense.Core.Services;
using PreySense.Frontend;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using PreySense.Frontend.Views;

namespace PreySense.Tests;

[TestClass]
[DoNotParallelize]
public class ReleasePackagingAndStartupTests
{
    private static readonly string[] RequiredBundleEntries =
    [
        "PresentationNative_cor3.dll",
        "wpfgfx_cor3.dll",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "vcruntime140_cor3.dll",
        "PreySense.dll",
        "PreySense.Core.dll",
        "PreySense.Overlay.dll",
        "PreySense.deps.json",
        "PreySense.runtimeconfig.json"
    ];

    internal readonly record struct BundleEntry(string RelativePath, byte Type, long Offset, long Size, long CompressedSize);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PreySense.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "PreySense.Frontend")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from " + AppContext.BaseDirectory);
    }

    internal static IReadOnlyList<BundleEntry> ReadSingleFileBundleEntries(string exePath)
    {
        // Only inspect the trailing tail of the executable where .NET places the BundleHeader + FileEntry table.
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int tailSize = (int)Math.Min(fs.Length, 256 * 1024);
        long tailStart = fs.Length - tailSize;
        byte[] tail = new byte[tailSize];
        fs.Seek(tailStart, SeekOrigin.Begin);
        int readTotal = 0;
        while (readTotal < tailSize)
        {
            int n = fs.Read(tail, readTotal, tailSize - readTotal);
            if (n == 0) break;
            readTotal += n;
        }

        for (int i = 0; i <= readTotal - 32; i++)
        {
            // .NET 6-10 single-file BundleHeader starts with MajorVersion=6 (or >=2), MinorVersion=0
            uint major = BitConverter.ToUInt32(tail, i);
            uint minor = BitConverter.ToUInt32(tail, i + 4);
            int fileCount = BitConverter.ToInt32(tail, i + 8);
            if (major < 2 || major > 10 || minor != 0 || fileCount is < 10 or > 5000)
                continue;

            int idLen = tail[i + 12];
            if (idLen is < 4 or > 64)
                continue;

            try
            {
                using var ms = new MemoryStream(tail, i, readTotal - i, writable: false);
                using var br = new BinaryReader(ms, Encoding.UTF8);
                uint maj = br.ReadUInt32();
                uint min = br.ReadUInt32();
                int count = br.ReadInt32();
                string bundleId = br.ReadString();
                if (string.IsNullOrWhiteSpace(bundleId))
                    continue;

                // DepsJson (offset, size), RuntimeConfigJson (offset, size), Flags
                _ = br.ReadInt64();
                _ = br.ReadInt64();
                _ = br.ReadInt64();
                _ = br.ReadInt64();
                _ = br.ReadUInt64();

                var entries = new List<BundleEntry>(count);
                for (int e = 0; e < count; e++)
                {
                    long offset = br.ReadInt64();
                    long size = br.ReadInt64();
                    long compressedSize = maj >= 6 ? br.ReadInt64() : size;
                    byte type = br.ReadByte();
                    string path = br.ReadString();
                    entries.Add(new BundleEntry(path, type, offset, size, compressedSize));
                }

                if (entries.Any(entry => entry.RelativePath.Equals("PreySense.deps.json", StringComparison.OrdinalIgnoreCase)))
                    return entries;
            }
            catch
            {
                // False positive candidate offset; continue scanning.
            }
        }

        throw new InvalidDataException($"Could not locate a valid .NET SingleFile BundleHeader in '{exePath}'.");
    }

    [TestMethod]
    public void FrontendProject_AndPublishScript_EnforceSingleFileNativeSelfExtract()
    {
        string repoRoot = FindRepoRoot();

        string csprojPath = Path.Combine(repoRoot, "PreySense.Frontend", "PreySense.Frontend.csproj");
        var doc = XDocument.Load(csprojPath);
        static string? Prop(XDocument xdoc, string name) =>
            xdoc.Root?.Elements("PropertyGroup").Elements(name).LastOrDefault()?.Value?.Trim();

        Assert.AreEqual("true", Prop(doc, "IncludeNativeLibrariesForSelfExtract"),
            "PreySense.Frontend.csproj must enable IncludeNativeLibrariesForSelfExtract so WPF native DLLs are never left outside the single-file exe.");
        Assert.AreEqual("true", Prop(doc, "IncludeAllContentForSelfExtract"),
            "PreySense.Frontend.csproj must enable IncludeAllContentForSelfExtract.");
        Assert.AreEqual("true", Prop(doc, "EnableCompressionInSingleFile"),
            "PreySense.Frontend.csproj must enable EnableCompressionInSingleFile.");
        StringAssert.Contains(Prop(doc, "RuntimeIdentifiers") ?? string.Empty, "win-x64");

        string publishScript = File.ReadAllText(Path.Combine(repoRoot, ".github", "publish.ps1"));
        StringAssert.Contains(publishScript, "-p:PublishSingleFile=true");
        StringAssert.Contains(publishScript, "-p:IncludeNativeLibrariesForSelfExtract=true");
        StringAssert.Contains(publishScript, "-p:IncludeAllContentForSelfExtract=true");
        StringAssert.Contains(publishScript, "-p:EnableCompressionInSingleFile=true");

        string releaseYml = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));
        StringAssert.Contains(releaseYml, "publish.ps1");
        StringAssert.Contains(releaseYml, nameof(ReleasePackagingAndStartupTests));
    }

    [TestMethod]
    public void PublishedReleaseExecutables_EmbedNativeWpfRuntime_WithNoLooseDlls()
    {
        string repoRoot = FindRepoRoot();
        string[] candidateDirs =
        [
            Path.Combine(repoRoot, "dist"),
            Path.Combine(repoRoot, "publish")
        ];

        var checkedExecutables = new List<string>();
        foreach (string dir in candidateDirs)
        {
            string exePath = Path.Combine(dir, "PreySense.exe");
            if (!File.Exists(exePath))
                continue;

            checkedExecutables.Add(exePath);

            // Loose runtime DLLs in publish/dist mask missing native self-extraction and make standalone downloads crash.
            string[] looseDlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.IsEmpty(looseDlls,
                $"'{dir}' contains loose .dll files next to PreySense.exe: {string.Join(", ", looseDlls)}. Native/runtime DLLs must be embedded inside PreySense.exe.");

            IReadOnlyList<BundleEntry> entries = ReadSingleFileBundleEntries(exePath);
            var byName = entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            string[] missing = RequiredBundleEntries.Where(req => !byName.ContainsKey(req)).ToArray();
            Assert.IsEmpty(missing,
                $"'{exePath}' is missing required single-file bundle entries: {string.Join(", ", missing)}. Users running PreySense.exe standalone will crash on startup.");

            foreach (string req in RequiredBundleEntries)
            {
                BundleEntry entry = byName[req];
                Assert.IsGreaterThan(0L, entry.Size, $"Bundle entry '{req}' in '{exePath}' has empty size.");
                Assert.IsGreaterThan(0L, entry.Offset, $"Bundle entry '{req}' in '{exePath}' has invalid offset.");
            }
        }

        if (checkedExecutables.Count == 0)
            Assert.Inconclusive("No published dist/PreySense.exe or publish/PreySense.exe found yet (pre-publish CI step).");
    }

    [TestMethod]
    public void CleanStartup_RealAppMainWindowAndToolWindows_InitializeWithHwndAndThemes()
    {
        if (Environment.GetEnvironmentVariable("PREYSENSE_CLEAN_STARTUP_CHILD") != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(ReleasePackagingAndStartupTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" +
                typeof(ReleasePackagingAndStartupTests).FullName + "." + nameof(CleanStartup_RealAppMainWindowAndToolWindows_InitializeWithHwndAndThemes));
            start.Environment["PREYSENSE_CLEAN_STARTUP_CHILD"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Isolated clean startup smoke test timed out.");
            }
            Assert.AreEqual(0, process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            return;
        }

        string tempWorkDir = Path.Combine(Path.GetTempPath(), "PreySenseCleanStartup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWorkDir);
        string originalDir = Environment.CurrentDirectory;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            Environment.CurrentDirectory = tempWorkDir;
            var app = new App();
            MainWindow? mainWindow = null;
            var createdWindows = new List<Window>();
            try
            {
                app.InitializeComponent();
                LocalizationService.Initialize();
                _ = ThemeManager.LoadAndApply();

                var hw = new MockHardwareService();
                mainWindow = new MainWindow(hw);
                createdWindows.Add(mainWindow);
                var vm = (MainViewModel)mainWindow.DataContext;
                vm.NotifyApplicationReady();

                // Exercise HWND creation, native WPF subclassing, DWM/WCA glass & opaque styling, and light/dark themes.
                IntPtr mainHwnd = new WindowInteropHelper(mainWindow).EnsureHandle();
                Assert.AreNotEqual(IntPtr.Zero, mainHwnd);

                ThemeManager.ApplyGlassEffect(true, persist: false);
                ThemeManager.Apply(isLight: false, persist: false);
                WindowSystemShadow.UpdateGlassEffect(mainWindow, glass: true, isLight: false);

                ThemeManager.ApplyGlassEffect(false, persist: false);
                ThemeManager.Apply(isLight: true, persist: false);
                WindowSystemShadow.UpdateGlassEffect(mainWindow, glass: false, isLight: true);

                Window[] toolWindows =
                [
                    new SettingsWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new ModeSettingsWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new FanCurveWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new KeyboardWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new LightingSettingsWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new MetricsWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new GamesWindow { Owner = mainWindow, DataContext = vm, ShowInTaskbar = false },
                    new CustomModalDialog { Owner = mainWindow, Message = "Smoke Test", ConfirmText = "OK", ShowCancelButton = false }
                ];

                foreach (var tool in toolWindows)
                {
                    createdWindows.Add(tool);
                    Assert.AreNotEqual(IntPtr.Zero, new WindowInteropHelper(tool).EnsureHandle(),
                        $"Failed to initialize HWND for {tool.GetType().Name}.");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                for (int i = createdWindows.Count - 1; i >= 0; i--)
                {
                    try { createdWindows[i].Close(); } catch { }
                }
                if (mainWindow?.DataContext is MainViewModel vm)
                {
                    try { vm.Dispose(); } catch { }
                }
                try { app.Shutdown(0); } catch { }
                Environment.CurrentDirectory = originalDir;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "STA UI initialization timed out.");
        try { Directory.Delete(tempWorkDir, recursive: true); } catch { }

        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
