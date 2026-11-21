using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SqlBranch.Core.Configuration;
using SqlBranch.Core.Images;
using SqlBranch.Core.Secrets;

namespace SqlBranch.Core.Containers;

/// <summary>The result of one process.</summary>
public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs a program. A seam so tests can check command lines without Docker.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program with these arguments and extra environment variables.
    /// Throws <see cref="Win32Exception"/> when the program does not exist.
    /// </summary>
    Task<ProcessOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        string? standardInput = null);

    /// <summary>
    /// Starts the program and returns at once with one of its standard streams as a <see cref="Stream"/>
    /// (see <see cref="IStreamingProcess"/>). The other streams are never buffered beyond a small cap on standard error.
    /// Throws <see cref="Win32Exception"/> when the program does not exist.
    /// </summary>
    IStreamingProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        StreamDirection direction,
        CancellationToken cancellationToken);
}

/// <summary>Which standard stream of a streaming process the caller uses.</summary>
public enum StreamDirection
{
    /// <summary>The caller reads the process's standard output. Its standard input is closed at once.</summary>
    Read,

    /// <summary>The caller writes the process's standard input. Its standard output is collected as text.</summary>
    Write,
}

/// <summary>
/// A running process whose standard output (<see cref="StreamDirection.Read"/>) or standard input
/// (<see cref="StreamDirection.Write"/>) is a <see cref="Stream"/>, so gigabytes pass through without being held in memory.
/// Standard error is always collected, up to a cap. Disposing it kills the process when it is still running and waits for it to exit.
/// </summary>
public interface IStreamingProcess : IAsyncDisposable
{
    /// <summary>The process's standard output to read, or its standard input to write.</summary>
    Stream Stream { get; }

    /// <summary>
    /// Closes standard input of a <see cref="StreamDirection.Write"/> process, waits for the process to exit and returns its exit code and
    /// collected text. When <paramref name="cancellationToken"/> ends first, the process is killed and awaited, then
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ProcessOutput> WaitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the process (and its children) when it is still running and returns only after it has exited.</summary>
    Task KillAsync();
}

/// <summary>Runs real processes. Standard input carries the given text, or is closed at once.</summary>
public sealed class SystemProcessRunner(bool inheritEnvironment = true) : IProcessRunner
{
    public async Task<ProcessOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (!inheritEnvironment)
        {
            info.Environment.Clear();
        }
        else
        {
            info.Environment.Remove(SettingKeys.SaPasswordVariable);
            info.Environment.Remove(BackupUrl.SasVariable);
            info.Environment.Remove(StorageSecrets.S3KeyIdVariable);
            info.Environment.Remove(StorageSecrets.S3SecretVariable);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new Win32Exception($"Could not start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between cancellation and the kill.
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        return new ProcessOutput(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public IStreamingProcess Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        StreamDirection direction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = direction == StreamDirection.Write ? Encoding.UTF8 : null,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        var process = Process.Start(info) ?? throw new Win32Exception($"Could not start '{fileName}'.");
        return new SystemStreamingProcess(process, direction, cancellationToken);
    }
}

/// <summary>A real process behind <see cref="IStreamingProcess"/>.</summary>
internal sealed class SystemStreamingProcess : IStreamingProcess
{
    /// <summary>The most standard error kept; a tool that prints more than this is not telling anything useful in the rest.</summary>
    private const int StdErrCap = 64 * 1024;

    private readonly Process _process;
    private readonly StreamDirection _direction;
    private readonly Task<string> _stderr;
    private readonly Task<string> _stdout;
    private readonly CancellationTokenRegistration _registration;
    private int _disposed;

    public SystemStreamingProcess(Process process, StreamDirection direction, CancellationToken cancellationToken)
    {
        _process = process;
        _direction = direction;
        _stderr = ReadCappedAsync(process.StandardError);
        if (direction == StreamDirection.Read)
        {
            process.StandardInput.Close();
            Stream = process.StandardOutput.BaseStream;
            _stdout = Task.FromResult(string.Empty);
        }
        else
        {
            Stream = process.StandardInput.BaseStream;
            _stdout = ReadCappedAsync(process.StandardOutput);
        }

        // The kill itself is awaited by whoever disposes this or calls WaitAsync; the registration only makes the process end.
        _registration = cancellationToken.Register(static state => ((SystemStreamingProcess)state!).Terminate(), this);
    }

    public Stream Stream { get; }

    public async Task<ProcessOutput> WaitAsync(CancellationToken cancellationToken)
    {
        if (_direction == StreamDirection.Write)
        {
            try
            {
                await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process already closed its input; its exit code says why.
            }
        }

        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAsync().ConfigureAwait(false);
            throw;
        }

        return new ProcessOutput(_process.ExitCode, await _stdout.ConfigureAwait(false), await _stderr.ConfigureAwait(false));
    }

    public async Task KillAsync()
    {
        Terminate();
        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(_stderr, _stdout).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _registration.DisposeAsync().ConfigureAwait(false);
        await KillAsync().ConfigureAwait(false);
        _process.Dispose();
    }

    private void Terminate()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (text.Length < StdErrCap)
            {
                text.Append(buffer, 0, Math.Min(read, StdErrCap - text.Length));
            }
        }

        return text.ToString();
    }
}
