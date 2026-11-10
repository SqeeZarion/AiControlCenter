using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AiControlCenter.LocalCopilot.Sidecar;

public sealed record CopilotDevicePrompt(string VerificationUri, string UserCode);
public sealed record CopilotLoginResult(int ExitCode, bool CredentialPresent, bool PlaintextStorageConfirmed);

public interface ICopilotLoginProcessRunner
{
    Task<CopilotLoginProcessResult> RunAsync(
        string copilotHome,
        Action<CopilotDevicePrompt> onDevicePrompt,
        CancellationToken cancellationToken);
    void StopProcess();
}

public sealed record CopilotLoginProcessResult(int ExitCode, bool PlaintextStorageConfirmed);

public sealed class SystemCopilotLoginProcessRunner : ICopilotLoginProcessRunner
{
    private Process? active;

    public async Task<CopilotLoginProcessResult> RunAsync(
        string copilotHome,
        Action<CopilotDevicePrompt> onDevicePrompt,
        CancellationToken cancellationToken)
    {
        using var process = CreateLoginProcess(copilotHome);
        active = process;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Copilot login process did not start.");
            var confirmationSent = 0;
            async Task ConfirmPlaintextStorage()
            {
                if (Interlocked.Exchange(ref confirmationSent, 1) != 0) return;
                await process.StandardInput.WriteLineAsync("y");
                await process.StandardInput.FlushAsync(cancellationToken);
            }

            var stdout = ObserveOutputAsync(process.StandardOutput, onDevicePrompt,
                ConfirmPlaintextStorage, cancellationToken);
            var stderr = ObserveOutputAsync(process.StandardError, onDevicePrompt,
                ConfirmPlaintextStorage, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            return new(process.ExitCode, confirmationSent != 0);
        }
        finally
        {
            active = null;
        }
    }

    public void StopProcess()
    {
        try
        {
            if (active is { HasExited: false }) active.Kill(true);
        }
        catch (InvalidOperationException) { }
    }

    private static Process CreateLoginProcess(string copilotHome)
    {
        var start = new ProcessStartInfo("/usr/local/bin/copilot")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "login", "--device-code", "--no-color" })
            start.ArgumentList.Add(argument);
        start.Environment["HOME"] = copilotHome;
        start.Environment["COPILOT_HOME"] = copilotHome;
        return new Process { StartInfo = start, EnableRaisingEvents = true };
    }

    private static async Task ObserveOutputAsync(
        StreamReader reader,
        Action<CopilotDevicePrompt> onDevicePrompt,
        Func<Task> confirmPlaintextStorage,
        CancellationToken cancellationToken)
    {
        var parser = new CopilotLoginOutputParser();
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return;
            var result = parser.Observe(buffer.AsSpan(0, read));
            if (result.DevicePrompt is not null) onDevicePrompt(result.DevicePrompt);
            if (result.PlaintextStoragePrompt) await confirmPlaintextStorage();
        }
    }
}
public enum CopilotLogoutStatus
{
    Succeeded,
    AlreadyUnauthenticated,
    UnsupportedCommand,
    TimedOut,
    Failed,
}

public sealed record CopilotLogoutResult(
    CopilotLogoutStatus Status,
    int? ExitCode,
    bool CredentialPresent);

public sealed record CopilotLogoutCommand(IReadOnlyList<string> Arguments, bool Interactive)
{
    public static CopilotLogoutCommand Select(bool directAuthLogoutSupported) =>
        directAuthLogoutSupported
            ? new(["auth", "logout"], Interactive: false)
            : new(["-i", "/logout", "--no-color"], Interactive: true);
}

public interface ICopilotCliRuntimeHost : IDisposable
{
    bool IsHeadlessReady { get; }
    bool HasStoredCredential { get; }
    string CopilotHome { get; }
    Task<CopilotLoginResult> LoginAsync(
        Action<CopilotDevicePrompt> onDevicePrompt,
        CancellationToken cancellationToken);
    Task StopHeadlessAsync(CancellationToken cancellationToken);
    Task<CopilotLogoutResult> LogoutAsync(CancellationToken cancellationToken);
    Task RestartHeadlessAsync(CancellationToken cancellationToken);
    void StopLogin();
}

public sealed class CopilotCliRuntimeHost(
    ICopilotLoginProcessRunner loginRunner,
    string? copilotHomeOverride = null,
    string? sessionWorkingDirectoryOverride = null) : ICopilotCliRuntimeHost
{
    public const string DefaultSessionWorkingDirectory = "/tmp/aicontrolcenter-session";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogoutProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LogoutExitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim runtimeGate = new(1, 1);
    private readonly string copilotHome = copilotHomeOverride
        ?? Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? "/var/lib/copilot";
    private readonly string sessionWorkingDirectory = sessionWorkingDirectoryOverride
        ?? DefaultSessionWorkingDirectory;
    private Process? headless;
    private volatile bool headlessReady;

    public bool IsHeadlessReady => headlessReady && headless is { HasExited: false };
    public bool HasStoredCredential => SafeFilePresent(Path.Combine(copilotHome, "config.json"));
    public string CopilotHome => copilotHome;

    public async Task<CopilotLoginResult> LoginAsync(
        Action<CopilotDevicePrompt> onDevicePrompt,
        CancellationToken cancellationToken)
    {
        var result = await loginRunner.RunAsync(copilotHome, onDevicePrompt, cancellationToken);
        return new(result.ExitCode, HasStoredCredential, result.PlaintextStorageConfirmed);
    }

    public async Task RestartHeadlessAsync(CancellationToken cancellationToken)
    {
        await runtimeGate.WaitAsync(cancellationToken);
        try
        {
            EnsureSessionWorkingDirectory(sessionWorkingDirectory);
            headlessReady = false;
            var previous = Interlocked.Exchange(ref headless, null);
            if (previous is not null)
                await StopAndDisposeAsync(previous, cancellationToken);

            var next = CreateProcess(["--headless", "--host", "0.0.0.0", "--port", "4321"]);
            if (!next.Start()) throw new InvalidOperationException("Copilot headless runtime did not start.");
            _ = DrainWithoutLoggingAsync(next.StandardOutput);
            _ = DrainWithoutLoggingAsync(next.StandardError);

            try
            {
                await WaitForReadinessAsync(next, cancellationToken);
                headless = next;
                headlessReady = true;
            }
            catch
            {
                await StopAndDisposeAsync(next, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            runtimeGate.Release();
        }
    }

    internal static void EnsureSessionWorkingDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("The Local Copilot session directory must be absolute.");
        Directory.CreateDirectory(path);
    }

    public async Task StopHeadlessAsync(CancellationToken cancellationToken)
    {
        await runtimeGate.WaitAsync(cancellationToken);
        try
        {
            headlessReady = false;
            var previous = Interlocked.Exchange(ref headless, null);
            if (previous is not null)
                await StopAndDisposeAsync(previous, cancellationToken);
        }
        finally
        {
            runtimeGate.Release();
        }
    }

    public async Task<CopilotLogoutResult> LogoutAsync(CancellationToken cancellationToken)
    {
        await runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (headless is { HasExited: false } || headlessReady)
                return new(CopilotLogoutStatus.Failed, null, HasStoredCredential);
            if (!HasStoredCredential)
                return new(CopilotLogoutStatus.AlreadyUnauthenticated, 0, CredentialPresent: false);

            var supportsDirectLogout = await SupportsDirectLogoutAsync(cancellationToken);
            var command = CopilotLogoutCommand.Select(supportsDirectLogout);
            using var process = CreateProcess(command.Arguments);
            if (!process.Start()) throw new InvalidOperationException("Copilot logout process did not start.");
            var observation = new CopilotLogoutOutputObservation();
            var stdout = ObserveLogoutOutputAsync(process.StandardOutput, observation);
            var stderr = ObserveLogoutOutputAsync(process.StandardError, observation);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LogoutTimeout);
            try
            {
                if (command.Interactive)
                    await WaitForInteractiveLogoutAsync(process, observation, timeout.Token);
                else
                    await process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException)
            {
                await TerminateProcessAsync(process);
                if (!cancellationToken.IsCancellationRequested)
                    return new(CopilotLogoutStatus.TimedOut, null, HasStoredCredential);
                throw;
            }
            await Task.WhenAll(stdout, stderr);
            var credentialPresent = HasStoredCredential;
            if (observation.AlreadyUnauthenticated)
                return new(CopilotLogoutStatus.AlreadyUnauthenticated,
                    process.HasExited ? process.ExitCode : 0, credentialPresent);
            if (observation.UnsupportedCommand)
                return new(CopilotLogoutStatus.UnsupportedCommand,
                    process.HasExited ? process.ExitCode : null, credentialPresent);
            if (!credentialPresent || observation.LogoutSucceeded
                || (!command.Interactive && process.HasExited && process.ExitCode == 0))
                return new(CopilotLogoutStatus.Succeeded,
                    process.HasExited ? process.ExitCode : 0, credentialPresent);
            return new(CopilotLogoutStatus.Failed,
                process.HasExited ? process.ExitCode : null, credentialPresent);
        }
        finally
        {
            runtimeGate.Release();
        }
    }

    private async Task<bool> SupportsDirectLogoutAsync(CancellationToken cancellationToken)
    {
        using var process = CreateProcess(["auth", "logout", "--help"]);
        if (!process.Start()) return false;
        var stdout = DrainWithoutLoggingAsync(process.StandardOutput);
        var stderr = DrainWithoutLoggingAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LogoutProbeTimeout);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessAsync(process);
            return false;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0;
    }

    private async Task WaitForInteractiveLogoutAsync(
        Process process,
        CopilotLogoutOutputObservation observation,
        CancellationToken cancellationToken)
    {
        while (!process.HasExited && HasStoredCredential
            && !observation.LogoutSucceeded && !observation.AlreadyUnauthenticated
            && !observation.UnsupportedCommand)
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

        if (!process.HasExited)
        {
            await process.StandardInput.WriteLineAsync("/exit");
            await process.StandardInput.FlushAsync(cancellationToken);
            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exitTimeout.CancelAfter(LogoutExitTimeout);
            try { await process.WaitForExitAsync(exitTimeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { await TerminateProcessAsync(process); }
        }
    }

    public void StopLogin() => loginRunner.StopProcess();

    private Process CreateProcess(IReadOnlyCollection<string> arguments)
    {
        var start = new ProcessStartInfo("/usr/local/bin/copilot")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["HOME"] = copilotHome;
        start.Environment["COPILOT_HOME"] = copilotHome;
        return new Process { StartInfo = start, EnableRaisingEvents = true };
    }

    private static async Task WaitForReadinessAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadyTimeout);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("Copilot headless runtime exited before readiness.");
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", 4321, timeout.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Copilot headless runtime readiness timed out."); }
    }

    private static async Task StopAndDisposeAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StopTimeout);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("Copilot headless runtime stop timed out."); }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DrainWithoutLoggingAsync(StreamReader reader)
    {
        var buffer = new char[256];
        while (await reader.ReadAsync(buffer.AsMemory()) != 0) { }
    }

    private static async Task ObserveLogoutOutputAsync(
        StreamReader reader,
        CopilotLogoutOutputObservation observation)
    {
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0) return;
            observation.Observe(buffer.AsSpan(0, read));
        }
    }

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException) { }
    }

    private static bool SafeFilePresent(string path)
    {
        try { return new FileInfo(path) is { Exists: true, Length: > 0 }; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        StopLogin();
        try { if (headless is { HasExited: false }) headless.Kill(true); }
        catch (InvalidOperationException) { }
        headless?.Dispose();
        runtimeGate.Dispose();
    }
}

public sealed class CopilotLogoutOutputObservation
{
    private const int MaximumWindow = 1024;
    private readonly object sync = new();
    private string window = string.Empty;
    private bool logoutSucceeded;
    private bool alreadyUnauthenticated;
    private bool unsupportedCommand;

    public bool LogoutSucceeded { get { lock (sync) return logoutSucceeded; } }
    public bool AlreadyUnauthenticated { get { lock (sync) return alreadyUnauthenticated; } }
    public bool UnsupportedCommand { get { lock (sync) return unsupportedCommand; } }

    public void Observe(ReadOnlySpan<char> chunk)
    {
        lock (sync)
        {
            window = string.Concat(window, chunk);
            if (window.Length > MaximumWindow) window = window[^MaximumWindow..];
            var normalized = window.ToLowerInvariant();
            logoutSucceeded = logoutSucceeded
                || normalized.Contains("logged out", StringComparison.Ordinal)
                || normalized.Contains("logout successful", StringComparison.Ordinal);
            alreadyUnauthenticated = alreadyUnauthenticated
                || normalized.Contains("not logged in", StringComparison.Ordinal)
                || normalized.Contains("already logged out", StringComparison.Ordinal)
                || normalized.Contains("not authenticated", StringComparison.Ordinal);
            unsupportedCommand = unsupportedCommand
                || normalized.Contains("invalid command", StringComparison.Ordinal)
                || normalized.Contains("unknown command", StringComparison.Ordinal)
                || normalized.Contains("not recognized", StringComparison.Ordinal);
        }
    }
}

public sealed partial class CopilotLoginOutputParser
{
    private const int MaximumWindow = 1024;
    private readonly object sync = new();
    private string window = string.Empty;
    private bool devicePromptReported;
    private bool plaintextPromptReported;

    public CopilotLoginOutputObservation Observe(ReadOnlySpan<char> chunk)
    {
        lock (sync)
        {
            window = string.Concat(window, chunk);
            if (window.Length > MaximumWindow) window = window[^MaximumWindow..];

            CopilotDevicePrompt? prompt = null;
            if (!devicePromptReported)
            {
                var match = DevicePrompt().Match(window);
                if (match.Success)
                {
                    devicePromptReported = true;
                    prompt = new(match.Groups["uri"].Value,
                        match.Groups["code"].Value.ToUpperInvariant());
                }
            }

            var plaintext = false;
            if (!plaintextPromptReported && PlaintextStoragePrompt().IsMatch(window))
            {
                plaintextPromptReported = true;
                plaintext = true;
            }
            return new(prompt, plaintext);
        }
    }

    [GeneratedRegex(@"(?<uri>https://github\.com/login/device)\s+and enter code\s+(?<code>[A-Z0-9]{4}-[A-Z0-9]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePrompt();

    [GeneratedRegex(@"(?:(?:Store|Save)\s+(?:the\s+)?(?:token|credential|it)[\s\S]{0,64}?plain\s*text(?:\s+file)?|(?:Store token in plaintext|Store credential in plaintext))\s*\??",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaintextStoragePrompt();
}

public sealed record CopilotLoginOutputObservation(
    CopilotDevicePrompt? DevicePrompt,
    bool PlaintextStoragePrompt);
