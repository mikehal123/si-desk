using System.Diagnostics;
using Sovereign.Desktop.Client;
using Xunit;

namespace Sovereign.Desktop.Tests;

public sealed class CodexWindowsShellTests
{
    [Fact]
    public void Store_pwsh_is_skipped_for_windows_powershell()
    {
        var selected = CodexWindowsShell.Select(
            [@"C:\ProgramData\Microsoft\WindowsApps", @"C:\Program Files\WindowsApps"],
            path => path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CodexWindowsShell.WindowsPowerShell, selected);
        Assert.False(CodexWindowsShell.IsStorePowerShell(selected));
    }

    [Fact]
    public void Machine_pwsh_is_used_when_path_only_has_the_store_copy()
    {
        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var selected = CodexWindowsShell.Select(
            [@"C:\ProgramData\Microsoft\WindowsApps"],
            path => path.Equals(machine, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(machine, selected);
        Assert.False(CodexWindowsShell.IsStorePowerShell(selected));
    }

    [Fact]
    public void A_non_store_pwsh_on_path_wins()
    {
        var custom = @"D:\Tools\pwsh\pwsh.exe";
        var selected = CodexWindowsShell.Select(
            [@"C:\ProgramData\Microsoft\WindowsApps", @"D:\Tools\pwsh"],
            path => path.Equals(custom, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(custom, selected);
    }

    [Fact]
    public void A_name_that_only_ends_with_windowsapps_is_kept()
    {
        var custom = @"D:\NotWindowsApps\pwsh.exe";
        var selected = CodexWindowsShell.Select([@"D:\NotWindowsApps"], path => path.Equals(custom, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(custom, selected);
        Assert.False(CodexWindowsShell.IsStorePowerShell(@"D:\NotWindowsApps"));
    }

    [Fact]
    public void Store_directories_leave_the_codex_path()
    {
        var stripped = CodexWindowsShell.WithoutStoreEntries(@"C:\Windows\System32;C:\ProgramData\Microsoft\WindowsApps;C:\Program Files\Git\cmd");
        Assert.DoesNotContain("WindowsApps", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Windows\System32", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Program Files\Git\cmd", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Connection_environment_hides_store_powershell_and_keeps_the_private_home()
    {
        var info = new ProcessStartInfo("codex.exe") { UseShellExecute = false };
        info.Environment["PATH"] = @"C:\Windows\System32;C:\ProgramData\Microsoft\WindowsApps";
        info.Environment["OPENAI_API_KEY"] = "secret";
        CodexChatClient.ApplyConnectionEnvironment(info, @"D:\private\codex-home");
        Assert.Equal(@"D:\private\codex-home", info.Environment["CODEX_HOME"]);
        Assert.False(info.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.DoesNotContain("WindowsApps", info.Environment["PATH"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Windows\System32", info.Environment["PATH"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_arguments_keep_the_sandbox_and_do_not_send_an_unknown_shell_key()
    {
        var flags = CodexChatClient.SessionArguments(new CursorProjectOptions());
        Assert.Contains("windows.sandbox=\"unelevated\"", flags);
        Assert.DoesNotContain(flags, flag => flag.Contains("shell_path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_windowsapps_pwsh_hit_is_not_replaced_by_powershell_51()
    {
        var choice = CodexWindowsShell.Choose(
            @"C:\ProgramData\Microsoft\WindowsApps\pwsh.exe" + "\r\n",
            CodexWindowsShell.WindowsPowerShell + "\r\n");
        Assert.True(choice.AliasStillVisible);
        Assert.Null(choice.Shell);
    }

    [Fact]
    public void A_real_pwsh_ahead_of_the_alias_is_the_shell_codex_would_start()
    {
        var real = @"C:\Program Files\PowerShell\7\pwsh.exe";
        var choice = CodexWindowsShell.Choose(real + "\r\n" + @"C:\ProgramData\Microsoft\WindowsApps\pwsh.exe", "");
        Assert.False(choice.AliasStillVisible);
        Assert.Equal(real, choice.Shell);
    }

    [Fact]
    public void Missing_pwsh_falls_through_to_windows_powershell()
    {
        var choice = CodexWindowsShell.Choose("INFO: Could not find files", CodexWindowsShell.WindowsPowerShell);
        Assert.False(choice.AliasStillVisible);
        Assert.Equal(CodexWindowsShell.WindowsPowerShell, choice.Shell);
    }
}
