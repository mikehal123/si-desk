using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Sovereign.Desktop.Client;
using Sovereign.Desktop.Core;
using Sovereign.Desktop.Client.Adapters;
using Sovereign.Desktop.Client.Scenarios;
using Sovereign.Desktop.Client.Storage;
using Sovereign.Desktop.Presentation;

namespace Sovereign.Desktop;

public partial class App : Application
{
    private MainWindow? _window;
    private ShellOpenHost? _shellOpen;
    private readonly List<string> _pendingShellFiles = [];
    private readonly object _shellOpenGate = new();
    private bool _shellWindowReady;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("APPDOMAIN " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog("UNOBSERVED " + e.Exception);
            e.SetObserved();
        };
        var firstChance = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var ex = e.Exception;
            var text = ex.GetType().Name + ": " + ex.Message;
            if (text.Contains("Cannot get credential from Vault", StringComparison.Ordinal)) return;
            var interesting = text.Contains("already", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Xaml", StringComparison.OrdinalIgnoreCase)
                || text.Contains("theme", StringComparison.OrdinalIgnoreCase)
                || text.Contains("parent", StringComparison.OrdinalIgnoreCase)
                || text.Contains("resource", StringComparison.OrdinalIgnoreCase)
                || text.Contains("null", StringComparison.OrdinalIgnoreCase)
                || ex is Microsoft.UI.Xaml.Markup.XamlParseException;
            if (!interesting && firstChance >= 40) return;
            if (!interesting) firstChance++;
            CrashLog("FIRST " + text);
        };
        SetCurrentProcessExplicitAppUserModelID(XaiDeskIdentity.AppUserModelId);
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CrashLog("XAML_UNHANDLED " + e.Message + Environment.NewLine + e.Exception);
            e.Handled = true;
        };
        if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_UI_POLISH") is not null)
            UnhandledException += (_, e) => WriteLaunchDiag("UI_POLISH_UNHANDLED " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var raw = Environment.GetCommandLineArgs();
        WriteLaunchDiag("OnLaunched raw_argc=" + raw.Length + " raw=" + string.Join(" | ", raw));
        WriteLaunchDiag("BaseDirectory=" + AppContext.BaseDirectory);
        try
        {
            var account = AccountConfiguration.Read(Path.Combine(AppContext.BaseDirectory, "account-service.json"));
            if (account.IsConfigured)
                account.Validate();
            var options = LaunchOptions.Parse(raw);
            var openedFiles = ShellOpenFiles.Parse(raw);
            WriteLaunchDiag(
                "parsed SmokeDump=" + (options.SmokeDump ?? "<null>") +
                " ExitAfterSmoke=" + options.ExitAfterSmoke +
                " Profile=" + (options.ProfileDirectory ?? "<null>") +
                " Mode=" + options.Mode +
                " HostProfile=" + (options.HostProfileDirectory ?? "<null>") +
                " OpenFiles=" + openedFiles.Count);
            if (!IsAutomationLaunch(options))
            {
                try
                {
                    _shellOpen = ShellOpenHost.Start(
                        ShellOpenFiles.InstanceKey(Environment.ProcessPath ?? AppContext.BaseDirectory),
                        openedFiles,
                        DeliverShellFile);
                }
                catch (Exception ex)
                {
                    WriteLaunchDiag("shell-open forward failed; opening here. " + ex.Message);
                    _shellOpen = null;
                }

                if (_shellOpen?.ExitProcess == true)
                {
                    WriteLaunchDiag("shell-open forwarded " + openedFiles.Count + " file(s) to the running window");
                    _shellOpen.Dispose();
                    _shellOpen = null;
                    Exit();
                    return;
                }
            }
            var profile = options.ProfileDirectory
                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                              "xAIDesk");
            profile = Path.GetFullPath(profile);
            if (options.OllamaChatVerification is not null || options.ChatLayoutVerification is not null || options.ChatSteeringVerification is not null) MainWindow.ValidateCodexQaProfile(profile);
            var installDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\') + "\\";
            if (profile.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PROFILE_INSIDE_INSTALL: the current-user profile must be outside the install directory.");
            }

            Directory.CreateDirectory(profile);
            var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
            string? hostProfile = null;

            WriteLaunchDiag("resolved profile=" + profile + " host=<null> fixtures=" + fixtures + " fixtures_exists=" + Directory.Exists(fixtures));
            IOperatorClient client = options.Mode switch
            {
                ApplicationMode.EvidenceArchive when options.ArchiveDirectory is not null =>
                    CreateArchive(options.ArchiveDirectory, fixtures),
                _ => new PreviewOperatorClient(ScenarioCatalog.Load(fixtures, options.ScenarioId ?? "admitted-blocked-execution"))
            };
            var vm = new ShellViewModel(client, new FileDraftStore(profile), profile, fixtures, hostProfile);
            Task.Run(() => vm.RefreshAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            WalkthroughReceipt? walkthrough = null;
            if (options.NativeIntegration)
            {
                walkthrough = Task.Run(() => NativeIntegrationWalkthrough.RunAsync(vm).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            }
            else if (options.Walkthrough)
            {
                walkthrough = Task.Run(() => ClosureWalkthrough.RunAsync(vm, fixtures).GetAwaiter().GetResult()).GetAwaiter().GetResult();
            }

            if (options.SmokeDump is not null)
            {
                WriteLaunchDiag("writing smoke dump to " + options.SmokeDump);
                var dumpDir = Path.GetDirectoryName(options.SmokeDump);
                if (!string.IsNullOrEmpty(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                }

                File.WriteAllText(options.SmokeDump, vm.StatusLine + Environment.NewLine + vm.ModeBanner + Environment.NewLine + vm.AccountHeading);
                WriteLaunchDiag("smoke dump write completed exists=" + File.Exists(options.SmokeDump));
                if (options.ExitAfterSmoke)
                {
                    Exit();
                    return;
                }
            }
            else
            {
                WriteLaunchDiag("SmokeDump is null; window path follows");
            }

        CrashLog("before MainWindow");
        MainWindow window;
        try
        {
            window = new MainWindow(vm, options.TestDpiPercent);
        }
        catch (Exception ex)
        {
            CrashLog("MAINWINDOW " + ex);
            throw;
        }
        CrashLog("after MainWindow");
        List<string> pendingShell;
        lock (_shellOpenGate)
        {
            _window = window;
            _shellWindowReady = true;
            pendingShell = [.. _pendingShellFiles];
            _pendingShellFiles.Clear();
        }
        window.Closed += (_, _) => _shellOpen?.Dispose();
        foreach (var path in openedFiles.Concat(pendingShell)) window.AcceptShellFile(path);
        var backgroundChatQa = options.ChatLayoutVerification is not null
            && Environment.GetEnvironmentVariable("SOVEREIGN_QA_BACKGROUND") == "1";
        var backgroundUpdateQa = Environment.GetEnvironmentVariable("SOVEREIGN_QA_BACKGROUND") == "1"
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOVEREIGN_UPDATE_READY"));
        if (backgroundUpdateQa) MainWindow.ValidateCodexQaProfile(profile);
        if (backgroundChatQa || backgroundUpdateQa)
        {
            _window.AppWindow.IsShownInSwitchers = false;
            _window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
            _window.AppWindow.Show(false);
        }
        else
        {
            CrashLog("before Activate");
            _window.Activate();
            CrashLog("after Activate");
        }
        if (options.ChatSteeringVerification is not null)
        {
            _ = _window.DispatcherQueue.TryEnqueue(async () => await _window.RunChatSteeringVerificationAsync(Path.GetFullPath(options.ChatSteeringVerification)));
        }
        else if (options.ChatLayoutVerification is not null)
        {
            _ = _window.DispatcherQueue.TryEnqueue(async () => {
                if (Environment.GetEnvironmentVariable("SOVEREIGN_QA_NATIVE_SUBAGENTS") == "1")
                    await _window.RunNativeSubagentsVerificationAsync(Path.GetFullPath(options.ChatLayoutVerification));
                else await _window.RunChatLayoutVerificationAsync(Path.GetFullPath(options.ChatLayoutVerification));
            });
        }
        else if (options.OllamaChatVerification is not null)
        {
            _ = _window.DispatcherQueue.TryEnqueue(async () => await _window.RunOllamaChatVerificationAsync(
                Path.GetFullPath(options.OllamaChatVerification), options.OllamaVerificationModel!, Path.GetFullPath(options.OllamaVerificationPrompts!), options.OllamaVerificationThinking));
        }
        else if (options.Walkthrough || options.NativeIntegration || options.KeyboardProbe || options.CaptureDirectory is not null)
        {
            var outDir = options.CaptureDirectory
                         ?? options.WalkthroughOut
                         ?? Path.Combine(profile, "walkthrough");
            Directory.CreateDirectory(outDir);
            var keyboard = options.KeyboardProbe;
            var exitAfter = options.ExitAfterWalkthrough;
            var receipt = walkthrough;
            if (receipt is not null)
            {
                File.WriteAllText(
                    Path.Combine(outDir, "walkthrough-receipt.json"),
                    System.Text.Json.JsonSerializer.Serialize(receipt, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            _ = _window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await _window.RunEvidenceCapture(outDir, keyboard, receipt);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(outDir, "capture-error.txt"), ex.ToString());
                }

                if (exitAfter)
                {
                    Environment.Exit(0);
                }
            });
        }
        else if (options.ExitAfterWalkthrough)
        {
            Exit();
        }
        }
        catch (Exception ex)
        {
            WriteLaunchDiag("EXCEPTION " + ex);
            CrashLog("ONLAUNCHED " + ex);
            throw;
        }
    }

    internal static void CrashLog(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xAIDesk");
            Directory.CreateDirectory(dir);
            var line = DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine;
            using var stream = new FileStream(Path.Combine(dir, "crash.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        catch
        {
        }
    }

    private void DeliverShellFile(string path)
    {
        MainWindow? window = null;
        lock (_shellOpenGate)
        {
            if (!_shellWindowReady || _window is null) _pendingShellFiles.Add(path);
            else window = _window;
        }

        window?.DispatcherQueue.TryEnqueue(() => window.AcceptShellFile(path));
    }

    private static bool IsAutomationLaunch(LaunchOptions options) =>
        options.ExitAfterSmoke || options.SmokeDump is not null || options.Walkthrough || options.KeyboardProbe
        || options.NativeIntegration || options.CaptureDirectory is not null || options.ExitAfterWalkthrough
        || options.ChatLayoutVerification is not null || options.ChatSteeringVerification is not null
        || options.OllamaChatVerification is not null;

    private static void WriteLaunchDiag(string message)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "xAIDesk.diag");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "startup.log"),
                DateTimeOffset.UtcNow.ToString("o") + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static IOperatorClient CreateArchive(string directory, string fixtures)
    {
        var packet = Sovereign.Desktop.Core.Archive.ArchiveReader.OpenDirectory(directory);
        if (packet.Manifest is null)
        {
            throw new InvalidOperationException(packet.Reason);
        }

        var snapshot = Path.Combine(directory, "snapshot.json");
        var scenario = File.Exists(snapshot)
            ? ScenarioCatalog.Parse(File.ReadAllText(snapshot))
            : ScenarioCatalog.Load(fixtures, "admitted-blocked-execution");
        return new ArchiveOperatorClient(scenario, packet);
    }
}

public sealed class LaunchOptions
{
    public ApplicationMode Mode { get; init; } = ApplicationMode.DesktopPreview;
    public string? ProfileDirectory { get; init; }
    public string? ScenarioId { get; init; }
    public string? ArchiveDirectory { get; init; }
    public string? SmokeDump { get; init; }
    public bool ExitAfterSmoke { get; init; }
    public bool Walkthrough { get; init; }
    public bool KeyboardProbe { get; init; }
    public bool ExitAfterWalkthrough { get; init; }
    public string? WalkthroughOut { get; init; }
    public string? CaptureDirectory { get; init; }
    public string? ChatLayoutVerification { get; init; }
    public string? ChatSteeringVerification { get; init; }
    public string? OllamaChatVerification { get; init; }
    public string? OllamaVerificationModel { get; init; }
    public string? OllamaVerificationPrompts { get; init; }
    public bool OllamaVerificationThinking { get; init; }
    public bool NativeIntegration { get; init; }
    public string? HostProfileDirectory { get; init; }
    public int TestDpiPercent { get; init; } = 100;

    public static LaunchOptions Parse(string[] args)
    {
        string? mode = null, profile = null, scenario = null, archive = null, smoke = null, walkOut = null, capture = null, hostProfile = null;
        string? chatLayoutVerification = null, chatSteeringVerification = null;
        string? ollamaChatVerification = null, ollamaVerificationModel = null, ollamaVerificationPrompts = null;
        var ollamaVerificationThinking = false;
        var exit = false;
        var walkthrough = false;
        var nativeIntegration = false;
        var keyboard = false;
        var exitWalk = false;
        var dpi = 100;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--mode": mode = Next(); break;
                case "--profile": profile = Next(); break;
                case "--scenario": scenario = Next(); break;
                case "--archive": archive = Next(); break;
                case "--smoke-dump": smoke = Next(); break;
                case "--exit-after-smoke": exit = true; break;
                case "--walkthrough": walkthrough = true; break;
                case "--native-integration": nativeIntegration = true; break;
                case "--keyboard-probe": keyboard = true; break;
                case "--exit-after-walkthrough": exitWalk = true; break;
                case "--walkthrough-out": walkOut = Next(); break;
                case "--capture-dir": capture = Next(); break;
                case "--chat-layout-verify": chatLayoutVerification = Next() ?? throw new ArgumentException("Missing layout evidence directory."); break;
                case "--chat-steering-verify": chatSteeringVerification = Next() ?? throw new ArgumentException("Missing chat steering evidence directory."); break;
                case "--ollama-chat-verify": ollamaChatVerification = Next() ?? throw new ArgumentException("Missing evidence directory."); break;
                case "--ollama-verify-model": ollamaVerificationModel = Next(); break;
                case "--ollama-verify-prompts": ollamaVerificationPrompts = Next(); break;
                case "--ollama-verify-thinking": ollamaVerificationThinking = true; break;
                case "--host-profile": hostProfile = Next(); break;
                case "--test-dpi":
                    _ = int.TryParse(Next(), out dpi);
                    break;
            }
        }

        if (ollamaChatVerification is not null && (string.IsNullOrWhiteSpace(ollamaVerificationModel) || string.IsNullOrWhiteSpace(ollamaVerificationPrompts)))
            throw new ArgumentException("Ollama verification requires an explicit model and exported prompts file.");
        // Host profile is resolved in OnLaunched. Pin profile_root/stage_root are ignored.

        var parsedMode = mode switch
        {
            "archive" => ApplicationMode.EvidenceArchive,
            "native" => ApplicationMode.NativeConnection,
            _ when nativeIntegration => ApplicationMode.NativeConnection,
            _ => ApplicationMode.DesktopPreview
        };
        return new LaunchOptions
        {
            Mode = parsedMode,
            ProfileDirectory = profile,
            ScenarioId = scenario,
            ArchiveDirectory = archive,
            SmokeDump = smoke,
            ExitAfterSmoke = exit,
            Walkthrough = walkthrough,
            NativeIntegration = nativeIntegration,
            KeyboardProbe = keyboard,
            ExitAfterWalkthrough = exitWalk,
            WalkthroughOut = walkOut,
            CaptureDirectory = capture,
            ChatLayoutVerification = chatLayoutVerification,
            ChatSteeringVerification = chatSteeringVerification,
            OllamaChatVerification = ollamaChatVerification,
            OllamaVerificationModel = ollamaVerificationModel,
            OllamaVerificationPrompts = ollamaVerificationPrompts,
            OllamaVerificationThinking = ollamaVerificationThinking,
            HostProfileDirectory = hostProfile,
            TestDpiPercent = dpi <= 0 ? 100 : dpi
        };
    }
}
