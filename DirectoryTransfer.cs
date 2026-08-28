using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace GameArchiver
{
    internal sealed class DirectoryTransferProgress
    {
        public long BytesCopied { get; set; }
        public long TotalBytes { get; set; }
        public string CurrentFile { get; set; }
    }

    internal static class DirectoryTransfer
    {
        public static void MoveSafely(string source, string destination, CancellationToken token, Action<DirectoryTransferProgress> progress = null)
        {
            source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Source folder does not exist: " + source);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Destination already exists: " + destination);
            if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Destination cannot be inside the source folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                progress?.Invoke(new DirectoryTransferProgress { BytesCopied = 0, TotalBytes = 1, CurrentFile = "Moving folder" });
                Directory.Move(source, destination);
                progress?.Invoke(new DirectoryTransferProgress { BytesCopied = 1, TotalBytes = 1, CurrentFile = "Complete" });
                return;
            }

            var staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
            var sourceTombstone = source + ".moving-" + Guid.NewGuid().ToString("N");
            var sourceRenamed = false;
            try
            {
                var totalBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
                long bytesCopied = 0;
                progress?.Invoke(new DirectoryTransferProgress { BytesCopied = 0, TotalBytes = totalBytes, CurrentFile = "Preparing transfer" });
                CopyTree(source, staging, token, totalBytes, ref bytesCopied, progress);
                token.ThrowIfCancellationRequested();
                // Renaming on the source volume is atomic. From this point, a
                // failed destination commit can put the source straight back.
                Directory.Move(source, sourceTombstone);
                sourceRenamed = true;
                Directory.Move(staging, destination);
                sourceRenamed = false;
                DeleteTree(sourceTombstone);
            }
            catch (Exception transferError)
            {
                Exception cleanupError = null;
                try
                {
                    if (sourceRenamed && Directory.Exists(sourceTombstone) && !Directory.Exists(source))
                    {
                        Directory.Move(sourceTombstone, source);
                    }
                    if (Directory.Exists(staging))
                    {
                        DeleteTree(staging);
                    }
                }
                catch (Exception ex)
                {
                    cleanupError = ex;
                }

                if (cleanupError != null)
                {
                    throw new AggregateException("The transfer failed and its temporary data could not be completely cleaned up.", transferError, cleanupError);
                }
                throw;
            }
        }

        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, true);
        }

        private static void CopyTree(string source, string destination, CancellationToken token, long totalBytes,
            ref long bytesCopied, Action<DirectoryTransferProgress> progress)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(destination, Path.GetFileName(file));
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        bytesCopied += read;
                        progress?.Invoke(new DirectoryTransferProgress
                        {
                            BytesCopied = bytesCopied,
                            TotalBytes = totalBytes,
                            CurrentFile = Path.GetFileName(file)
                        });
                    }
                }
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                File.SetAttributes(target, File.GetAttributes(file));
            }
            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                token.ThrowIfCancellationRequested();
                CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), token, totalBytes, ref bytesCopied, progress);
            }
            Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
        }
    }
}
