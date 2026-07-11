using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CatapiController;

internal sealed record DesktopSessionState(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string AccountLabel);

internal interface IDesktopStateReader
{
    Task<DesktopSessionState> ReadAsync(string gatewayPath, CancellationToken ct);
}

internal sealed class DesktopStateReader : IDesktopStateReader
{
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan KillExitTimeout = TimeSpan.FromSeconds(1);
    private const int MaxOutputCharacters = 64 * 1024;

    private readonly Func<string, CancellationToken, Task<string>> _outputReader;

    public DesktopStateReader(
        Func<string, CancellationToken, Task<string>>? outputReader = null)
    {
        _outputReader = outputReader ?? ReadProcessOutputAsync;
    }

    public async Task<DesktopSessionState> ReadAsync(
        string gatewayPath,
        CancellationToken ct)
    {
        var json = await _outputReader(gatewayPath, ct);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Count() != 4)
        {
            throw new InvalidDataException();
        }

        return new DesktopSessionState(
            RequiredString(root, "token"),
            RequiredString(root, "refreshToken"),
            RequiredString(root, "userMis"),
            RequiredString(root, "accountLabel"));
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException();
        }

        return value.GetString()!;
    }

    private static async Task<string> ReadProcessOutputAsync(
        string gatewayPath,
        CancellationToken ct)
    {
        Process? process = null;
        CancellationTokenSource? operationCancellation = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            var scriptPath = Path.Combine(gatewayPath, "src", "catpawState.js");
            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = gatewayPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(scriptPath);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException();
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operationCancellation.CancelAfter(ImportTimeout);
            stdoutTask = ReadBoundedAsync(
                process.StandardOutput, operationCancellation.Token);
            stderrTask = ReadBoundedAsync(
                process.StandardError, operationCancellation.Token);
            await process.WaitForExitAsync(operationCancellation.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException();
            }

            return stdout;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("Catpaw desktop state reader failed.");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(KillExitTimeout);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or TimeoutException)
                {
                }
                finally
                {
                    operationCancellation?.Cancel();
                    await ObserveAsync(stdoutTask);
                    await ObserveAsync(stderrTask);
                    process.Dispose();
                }
            }

            operationCancellation?.Dispose();
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length + read > MaxOutputCharacters)
            {
                throw new InvalidDataException();
            }

            output.Append(buffer, 0, read);
        }
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch
        {
        }
    }
}
