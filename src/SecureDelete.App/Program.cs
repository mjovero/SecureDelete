using System.Security.Cryptography;
using System.Text;

namespace SecureDelete;

internal sealed record CliOptions(int Passes, bool Recursive, bool Force, IReadOnlyList<string> Targets)
{
    public static CliOptions? Parse(string[] args)
    {
        var targets = new List<string>();
        var passes = 3;
        var recursive = false;
        var force = false;

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
                case "--help" or "-h" or "-?":
                    return null;
                default:
                    targets.Add(current);
                    break;
            }
        }

        return targets.Count == 0 ? null : new CliOptions(passes, recursive, force, targets);
    }
}

internal sealed class SecureWipeResult
{
    public List<string> Deleted { get; } = new();
    public List<(string Target, string Reason)> Failed { get; } = new();
    public bool Success => Failed.Count == 0;
}

internal static class Program
{
    private const int DefaultBufferSize = 1024 * 64;

    public static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return 1;
        }

        var deleter = new SecureFileDeleter(options.Passes, options.Recursive, options.Force);
        var result = deleter.WipeTargets(options.Targets);

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
        builder.AppendLine("  -h, --help               Show this help text");
        builder.AppendLine();
        builder.AppendLine("Examples:");
        builder.AppendLine("  SecureDelete.exe --passes 5 --recursive C:\\Sensitive\\Archive");
        builder.AppendLine("  SecureDelete.exe -p 2 C:\\Temp\\file.txt D:\\logs\\old.log");

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

    public SecureWipeResult WipeTargets(IEnumerable<string> targets)
    {
        var result = new SecureWipeResult();

        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    if (!_recursive)
                    {
                        result.Failed.Add((target, "Directory specified without --recursive."));
                        continue;
                    }

                    WipeDirectory(target, result);
                    TryDeleteDirectory(target, result);
                }
                else if (File.Exists(target))
                {
                    WipeFile(target, result);
                }
                else
                {
                    result.Failed.Add((target, "Target does not exist."));
                }
            }
            catch (Exception ex)
            {
                if (!result.Failed.Exists(f => string.Equals(f.Target, target, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Failed.Add((target, ex.Message));
                }

                if (!_force)
                {
                    break;
                }
            }
        }

        return result;
    }

    private void WipeDirectory(string path, SecureWipeResult result)
    {
        foreach (var file in Directory.GetFiles(path))
        {
            WipeFile(file, result);
        }

        foreach (var directory in Directory.GetDirectories(path))
        {
            if (!_recursive)
            {
                result.Failed.Add((directory, "Nested directory skipped without --recursive."));
                continue;
            }

            WipeDirectory(directory, result);
            TryDeleteDirectory(directory, result);
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
