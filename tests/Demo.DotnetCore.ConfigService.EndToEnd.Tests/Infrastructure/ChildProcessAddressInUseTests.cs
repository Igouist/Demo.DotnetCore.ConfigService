using System.Diagnostics;
using System.Reflection;

namespace Demo.DotnetCore.ConfigService.EndToEnd.Tests.Infrastructure;

[Collection(EndToEndCollection.Name)]
public sealed class ChildProcessAddressInUseTests
{
    [Fact]
    public async Task Address_in_use_classification_observes_output_within_the_launch_deadline()
    {
        await using TemporaryDirectory scenarioDirectory =
            TemporaryDirectory.Create("config-service-e2e-address-in-use-");
        (string fileName, string[] arguments) = CreateMarkerProcessCommand();
        IReadOnlyDictionary<string, string> environment =
            ProcessEnvironment.CreateApplication(scenarioDirectory.Path);

        await using ChildProcess process = ChildProcess.Start(new ChildProcessStartOptions(
            "Live address-in-use output regression",
            fileName,
            scenarioDirectory.Path,
            Path.Combine(scenarioDirectory.Path, "marker-child.log"),
            arguments,
            environment));

        bool addressInUse = await process.WaitForAddressInUseOutputAsync(
                TimeSpan.FromSeconds(15))
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(addressInUse);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task Address_in_use_classification_returns_false_when_exited_output_completion_faults()
    {
        using Process rawProcess = StartExitedProcess();
        var logWriter = new StreamWriter(new ThrowingWriteStream()) { AutoFlush = true };
        ChildProcess process = CreateChildProcess(rawProcess, logWriter);

        try
        {
            bool addressInUse = await process.WaitForAddressInUseOutputAsync(
                TimeSpan.FromSeconds(15));

            Assert.False(addressInUse);
        }
        finally
        {
            try
            {
                await process.DisposeAsync();
            }
            catch (IOException)
            {
            }

            try
            {
                logWriter.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private static (string FileName, string[] Arguments) CreateMarkerProcessCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            string executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            return (
                executable,
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::Out.WriteLine('AddressInUseException'); Start-Sleep -Seconds 5"
                ]);
        }

        return (
            "/bin/sh",
            ["-c", "printf '%s\\n' AddressInUseException; sleep 5"]);
    }

    private static ChildProcess CreateChildProcess(Process process, StreamWriter logWriter)
    {
        ConstructorInfo constructor = typeof(ChildProcess).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(Process), typeof(StreamWriter)],
            modifiers: null)
            ?? throw new InvalidOperationException("ChildProcess constructor was not found.");

        return (ChildProcess)constructor.Invoke(["Faulting output completion regression", process, logWriter]);
    }

    private static Process StartExitedProcess()
    {
        (string FileName, string[] Arguments) command = OperatingSystem.IsWindows()
            ? (
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "exit 1"])
            : ("/bin/sh", ["-c", "exit 1"]);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Faulting output completion child did not start.");
        }

        return process;
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Controlled retained-log failure.");
    }
}
