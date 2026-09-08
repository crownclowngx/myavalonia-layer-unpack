using System.Security.Cryptography;
using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Infrastructure;

/// <summary>只在本次私有暂存目录内捕获提交凭据。此处允许枚举，因为目录的全部内容均属于当前事务，
/// 不会把输出根中的历史文件纳入结果；提交后整理只消费这份显式清单。</summary>
internal static class CommittedManifest
{
    internal static async Task<IReadOnlyList<CommittedEntry>> CaptureAsync(string root, int maxEntries, CancellationToken token)
    {
        var entries = new List<CommittedEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            PathPolicy.EnsureNoLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= maxEntries)
                    throw new UnpackFailureException(UnpackError.BudgetExceeded, "提交清单超过条目预算。", true);
                PathPolicy.EnsureNoLinks(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Device) != 0)
                    throw new UnpackFailureException(UnpackError.UnsafePath, "提交内容包含特殊文件。", true);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (isDirectory)
                {
                    entries.Add(new(relative, true, 0, Directory.GetLastWriteTimeUtc(path), Directory.GetCreationTimeUtc(path), ""));
                    pending.Push(path);
                }
                else
                {
                    // 哈希函数自带租用缓冲，关闭 FileStream 的第二层缓冲，避免大量小文件累计分配大数组。
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    entries.Add(new(relative, false, stream.Length, File.GetLastWriteTimeUtc(path), File.GetCreationTimeUtc(path), hash));
                }
            }
        }
        return Array.AsReadOnly(entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray());
    }
}
