using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SecureDelete;

internal sealed record CliOptions(int Passes, bool Recursive, bool Force, bool WindowUi, IReadOnlyList<string> Targets)
{
    public static CliOptions? Parse(string[] args)
    {
        var targets = new List<string>();
        var passes = 3;
        var recursive = false;
        var force = false;
        var windowUi = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            switch (current)
            {
                case "--passes" or "-p":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out passes) || passes < 1)
                    {
                        Console.Error.WriteLine("--passes requires a positive integer.");
                        return null;
                    }

                    i++;
                    break;
                case "--recursive" or "-r":
                    recursive = true;
                    break;
                case "--force" or "-f":
                    force = true;
                    break;
                case "--window-ui":
                    windowUi = true;
                    break;
                case "--help" or "-h" or "-?":
                    return null;
                default:
                    targets.Add(current);
                    break;
            }
        }

        return targets.Count == 0 ? null : new CliOptions(passes, recursive, force, windowUi, targets);
    }
}

internal sealed class SecureWipeResult
{
    public List<string> Deleted { get; } = new();
    public List<(string Target, string Reason)> Failed { get; } = new();
    public bool Success => Failed.Count == 0;
}

internal sealed record SecureWipePlan(List<string> Files, List<string> Directories);

internal sealed record ProgressUpdate(int Completed, int Total, string CurrentTarget);

internal interface ISecureDeleteProgress
{
    void Initialize(int totalItems);
    void Report(ProgressUpdate update);
    void Complete();
}

internal sealed class ConsoleProgressBar : ISecureDeleteProgress
{
    private int _totalItems;

    public void Initialize(int totalItems)
    {
        _totalItems = totalItems;
        Render(0, string.Empty);
    }

    public void Report(ProgressUpdate update) => Render(update.Completed, update.CurrentTarget);

    public void Complete()
    {
        Console.WriteLine();
    }

    private static int ConsoleWidthSafe
    {
        get
        {
            try
            {
                return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
            }
            catch
            {
                return 80;
            }
        }
    }

    private void Render(int completed, string currentTarget)
    {
        var width = Math.Max(40, ConsoleWidthSafe - 20);
        var clampedTotal = Math.Max(_totalItems, 1);
        var percent = _totalItems == 0 ? 100 : (int)Math.Round((double)completed / clampedTotal * 100, MidpointRounding.AwayFromZero);
        var filled = (int)Math.Min(width, Math.Round(width * percent / 100d, MidpointRounding.AwayFromZero));
        var bar = new string('#', filled).PadRight(width, '.');
        var label = string.IsNullOrWhiteSpace(currentTarget) ? string.Empty : $" {Path.GetFileName(currentTarget)}";

        Console.Write($"\r[{bar}] {percent,3}% ({completed}/{_totalItems}){label}");
    }
}

internal sealed class ProgressDialog : Form, ISecureDeleteProgress
{
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _targetLabel;
    private bool _canClose;
    private int _totalItems = 1;

    public ProgressDialog()
    {
        Text = "SecureDelete";
        Width = 420;
        Height = 180;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _statusLabel = new Label
        {
            Text = "Preparing secure deletion...",
            AutoSize = true,
            Left = 15,
            Top = 20,
            Width = 360
        };

        _progressBar = new ProgressBar
        {
            Left = 15,
            Top = 50,
            Width = 370,
            Height = 24,
            Minimum = 0,
            Maximum = 1,
            Style = ProgressBarStyle.Continuous
        };

        _targetLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Left = 15,
            Top = 85,
            Width = 360
        };

        Controls.AddRange(new Control[] { _statusLabel, _progressBar, _targetLabel });
        _canClose = false;
    }

    public void AttachTask(Task<SecureWipeResult> wipeTask)
    {
        wipeTask.ContinueWith(task =>
        {
            var result = task.Result;
            var message = result.Success
                ? "Secure deletion complete."
                : $"Completed with {result.Failed.Count} error(s).";

            UpdateUi(() =>
            {
                _canClose = true;
                ControlBox = true;
                _statusLabel.Text = message;
                _targetLabel.Text = string.Empty;
                _progressBar.Value = Math.Min(_progressBar.Maximum, _progressBar.Value);
                MessageBox.Show(this, message, "SecureDelete", result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                Close();
            });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Initialize(int totalItems)
    {
        _totalItems = Math.Max(totalItems, 1);
        UpdateUi(() =>
        {
            ControlBox = false;
            _progressBar.Maximum = _totalItems;
            _progressBar.Value = 0;
            _statusLabel.Text = $"Starting... (0/{_totalItems})";
        });
    }

    public void Report(ProgressUpdate update)
    {
        UpdateUi(() =>
        {
            _progressBar.Value = Math.Clamp(update.Completed, _progressBar.Minimum, _progressBar.Maximum);
            _statusLabel.Text = $"Deleting securely ({update.Completed}/{_totalItems})";
            _targetLabel.Text = string.IsNullOrWhiteSpace(update.CurrentTarget)
                ? string.Empty
                : $"Current: {Path.GetFileName(update.CurrentTarget)}";
        });
    }

    public void Complete()
    {
        UpdateUi(() =>
        {
            _statusLabel.Text = "Finalizing secure deletion...";
        });
    }

    private void UpdateUi(Action update)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (!IsHandleCreated)
        {
            try
            {
                _ = Handle;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(update);
            }
            catch (InvalidOperationException)
            {
                // The form was closed; ignore further updates.
            }
        }
        else
        {
            if (!IsDisposed && !Disposing)
            {
                update();
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_canClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }
}

internal static class Program
{
    private const int DefaultBufferSize = 1024 * 64;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return 1;
        }

        var deleter = new SecureFileDeleter(options.Passes, options.Recursive, options.Force);
        return options.WindowUi
            ? RunWithWindowUi(options, deleter)
            : RunWithConsoleUi(options, deleter);
    }

    private static int RunWithConsoleUi(CliOptions options, SecureFileDeleter deleter)
    {
        var progress = new ConsoleProgressBar();
        var result = deleter.WipeTargets(options.Targets, progress);

        foreach (var entry in result.Deleted)
        {
            Console.WriteLine($"Deleted securely: {entry}");
        }

        foreach (var (target, reason) in result.Failed)
        {
            Console.Error.WriteLine($"Failed to delete {target}: {reason}");
        }

        return result.Success ? 0 : 1;
    }

    private static int RunWithWindowUi(CliOptions options, SecureFileDeleter deleter)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var progressDialog = new ProgressDialog();
        var wipeTask = Task.Run(() => deleter.WipeTargets(options.Targets, progressDialog));

        progressDialog.AttachTask(wipeTask);
        Application.Run(progressDialog);

        var result = wipeTask.GetAwaiter().GetResult();
        return result.Success ? 0 : 1;
    }

    private static void PrintUsage()
    {
        var builder = new StringBuilder();
        builder.AppendLine("SecureDelete - secure file wiping utility");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  SecureDelete [options] <fileOrDirectory> [additional targets...]");
        builder.AppendLine();
        builder.AppendLine("Options:");
        builder.AppendLine("  -p, --passes <number>    Number of overwrite passes (default: 3)");
        builder.AppendLine("  -r, --recursive          Recursively wipe directories");
        builder.AppendLine("  -f, --force              Ignore read-only attributes and continue on errors");
        builder.AppendLine("      --window-ui          Show a windowed progress indicator (used by File Explorer context menu)");
        builder.AppendLine("  -h, --help               Show this help text");
        builder.AppendLine();
        builder.AppendLine("Note: On HDDs, multi-pass overwrites are effective. On SSDs, wear leveling/TRIM may leave");
        builder.AppendLine("residual data; combine with full-disk encryption and drive secure-erase utilities.");
        builder.AppendLine();
        builder.AppendLine("Examples:");
        builder.AppendLine("  SecureDelete.exe --passes 5 --recursive C:\\Sensitive\\Archive");
        builder.AppendLine("  SecureDelete.exe -p 2 C:\\Temp\\file.txt D:\\logs\\old.log");
        builder.AppendLine("  SecureDelete.exe --window-ui \"%1\"  (File Explorer context menu)");

        Console.WriteLine(builder.ToString());
    }
}

internal sealed class SecureFileDeleter
{
    private readonly int _passes;
    private readonly bool _recursive;
    private readonly bool _force;

    public SecureFileDeleter(int passes, bool recursive, bool force)
    {
        _passes = passes;
        _recursive = recursive;
        _force = force;
    }

    public SecureWipeResult WipeTargets(IEnumerable<string> targets, ISecureDeleteProgress? progress = null)
    {
        var result = new SecureWipeResult();
        var plan = BuildPlan(targets, result);

        if (!_force && result.Failed.Count > 0)
        {
            return result;
        }

        progress?.Initialize(plan.Files.Count);
        var processed = 0;

        foreach (var file in plan.Files)
        {
            var failedBefore = result.Failed.Count;
            try
            {
                WipeFile(file, result);
            }
            catch (Exception ex)
            {
                if (!result.Failed.Exists(f => string.Equals(f.Target, file, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Failed.Add((file, ex.Message));
                }
            }

            processed++;
            progress?.Report(new ProgressUpdate(processed, plan.Files.Count, file));

            if (!_force && result.Failed.Count > failedBefore)
            {
                break;
            }
        }

        foreach (var directory in plan.Directories)
        {
            var failedBefore = result.Failed.Count;
            try
            {
                TryDeleteDirectory(directory, result);
            }
            catch (Exception ex)
            {
                if (!result.Failed.Exists(f => string.Equals(f.Target, directory, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Failed.Add((directory, ex.Message));
                }
            }

            if (!_force && result.Failed.Count > failedBefore)
            {
                break;
            }
        }

        progress?.Complete();

        return result;
    }

    private SecureWipePlan BuildPlan(IEnumerable<string> targets, SecureWipeResult result)
    {
        var files = new List<string>();
        var directories = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    if (!_recursive)
                    {
                        result.Failed.Add((target, "Directory specified without --recursive."));

                        if (!_force)
                        {
                            break;
                        }

                        continue;
                    }

                    CollectDirectoryEntries(target, files, directories, result);
                    directories.Add(target);
                }
                else if (File.Exists(target))
                {
                    files.Add(target);
                }
                else
                {
                    result.Failed.Add((target, "Target does not exist."));

                    if (!_force)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Failed.Add((target, ex.Message));

                if (!_force)
                {
                    break;
                }
            }
        }

        return new SecureWipePlan(files, directories);
    }

    private void CollectDirectoryEntries(string path, List<string> files, List<string> directories, SecureWipeResult result)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path))
            {
                files.Add(file);
            }

            foreach (var directory in Directory.GetDirectories(path))
            {
                CollectDirectoryEntries(directory, files, directories, result);
                directories.Add(directory);
            }
        }
        catch (Exception ex)
        {
            result.Failed.Add((path, ex.Message));

            if (!_force)
            {
                throw;
            }
        }
    }

    private void WipeFile(string path, SecureWipeResult result)
    {
        try
        {
            OverwriteFile(path);
            var renamedPath = RenameRandomly(path);
            File.Delete(renamedPath);
            result.Deleted.Add(path);
        }
        catch (Exception ex)
        {
            result.Failed.Add((path, ex.Message));
            if (!_force)
            {
                throw;
            }
        }
    }

    private void OverwriteFile(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("File not found.", path);
        }

        if ((fileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
        {
            fileInfo.Attributes = FileAttributes.Normal;
        }

        var length = fileInfo.Length;
        if (length == 0)
        {
            return;
        }

        var bufferSize = (int)Math.Min(Math.Max(DefaultBufferSize, 4096), length);
        var buffer = new byte[bufferSize];

        using var random = RandomNumberGenerator.Create();

        for (var pass = 0; pass < _passes; pass++)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize, FileOptions.WriteThrough | FileOptions.SequentialScan);
            var remaining = length;

            while (remaining > 0)
            {
                random.GetBytes(buffer);
                var toWrite = (int)Math.Min(buffer.Length, remaining);
                stream.Write(buffer, 0, toWrite);
                remaining -= toWrite;
            }

            stream.Flush(true);
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(0);
            stream.Flush(true);
        }
    }

    private string RenameRandomly(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var randomName = $"{Guid.NewGuid():N}.del";
        var targetPath = Path.Combine(directory, randomName);

        File.Move(path, targetPath, overwrite: false);
        return targetPath;
    }

    private void TryDeleteDirectory(string path, SecureWipeResult result)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            result.Deleted.Add(path);
        }
        catch (Exception ex)
        {
            result.Failed.Add((path, ex.Message));
            if (!_force)
            {
                throw;
            }
        }
    }
}
