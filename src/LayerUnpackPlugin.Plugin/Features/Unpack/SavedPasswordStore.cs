using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Features.Unpack;

public sealed record SavedPassword(Guid Id, string Hint, bool IsDefault)
{
    public override string ToString() => IsDefault ? Hint + "（默认）" : Hint;
}

public interface ISavedPasswordStore
{
    bool IsAvailable { get; }
    IReadOnlyList<SavedPassword> List();
    string ReadPassword(Guid id);
    Guid Save(Guid? id, string hint, string password, bool isDefault);
    void Remove(Guid id);
}

internal sealed class SavedPasswordStore(string path) : ISavedPasswordStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly object _gate = new();
    public bool IsAvailable => OperatingSystem.IsWindows();

    public IReadOnlyList<SavedPassword> List()
    {
        lock (_gate) return Load().Select(entry => new SavedPassword(entry.Id, entry.Hint, entry.IsDefault)).ToArray();
    }

    public string ReadPassword(Guid id)
    {
        lock (_gate)
        {
            var entry = Load().FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("所选常用密码已被移除，请重新选择。");
            var bytes = Transform(Convert.FromBase64String(entry.Secret), protect: false);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public Guid Save(Guid? id, string hint, string password, bool isDefault)
    {
        hint = hint.Trim();
        if (hint.Length is < 1 or > 60 || hint.Any(char.IsControl))
            throw new ArgumentException("请输入 1 到 60 个字符的提示关键词。");
        var identifier = id ?? Guid.NewGuid();
        Update(entries =>
        {
            var existing = entries.FirstOrDefault(entry => entry.Id == identifier);
            if (id.HasValue && existing is null) throw new InvalidOperationException("此条目已被移除，请刷新列表。");
            if (existing is null && entries.Count >= 64) throw new InvalidOperationException("最多保存 64 个常用密码。");
            if (entries.Any(entry => entry.Id != identifier && string.Equals(entry.Hint, hint, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("此提示关键词已存在，请换一个。");
            if (password.Length > 0 && string.Equals(hint, password, StringComparison.Ordinal))
                throw new ArgumentException("提示关键词不能与密码相同。");
            var encrypted = password.Length == 0 ? existing?.Secret : Protect(password);
            if (encrypted is null) throw new ArgumentException("请输入要保存的解压密码。");
            if (isDefault)
                for (var index = 0; index < entries.Count; index++)
                    entries[index] = entries[index] with { IsDefault = false };
            var replacement = new StoredPassword(identifier, hint, encrypted, isDefault);
            if (existing is null) entries.Add(replacement);
            else entries[entries.FindIndex(entry => entry.Id == identifier)] = replacement;
        });
        return identifier;
    }

    public void Remove(Guid id) => Update(entries => entries.RemoveAll(entry => entry.Id == id));

    private List<StoredPassword> Load()
    {
        if (!IsAvailable) throw new PlatformNotSupportedException();
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("密码库超出大小限制。");
            var entries = JsonSerializer.Deserialize<List<StoredPassword>>(stream) ?? throw new InvalidDataException();
            if (entries.Count > 64 || entries.Any(entry => entry is null || entry.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.Hint) || string.IsNullOrWhiteSpace(entry.Secret)) ||
                entries.Select(entry => entry.Id).Distinct().Count() != entries.Count || entries.Count(entry => entry.IsDefault) > 1)
                throw new InvalidDataException("密码库内容不完整。");
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private void Update(Action<List<StoredPassword>> change)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException();
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // An independent Host and Standalone can both open the library. Serialize read/modify
            // across processes and publish with a same-directory rename.
            using var processLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var entries = Load();
            change(entries);
            var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, entries);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(staging, _path, overwrite: true);
            }
            finally { File.Delete(staging); }
        }
    }

    private static string Protect(string password)
    {
        PasswordPool.Normalize([password]);
        // Windows DPAPI is scoped to the current user. Only the ciphertext is persisted.
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(Transform(bytes, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        DataBlob output = default;
        try
        {
            var input = new DataBlob(bytes.Length, handle.AddrOfPinnedObject());
            const uint noUi = 1;
            var succeeded = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, noUi, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, noUi, out output);
            if (!succeeded) throw new CryptographicException(Marshal.GetLastPInvokeError());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            handle.Free();
            if (output.Data != IntPtr.Zero)
            {
                if (!protect)
                    for (var index = 0; index < output.Length; index++) Marshal.WriteByte(output.Data, index, 0);
                LocalFree(output.Data);
            }
        }
    }

    private sealed record StoredPassword(Guid Id, string Hint, string Secret, bool IsDefault);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;
        public readonly IntPtr Data = data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
