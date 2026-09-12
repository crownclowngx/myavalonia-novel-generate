using System.Security.Cryptography;
using System.Text;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Infrastructure.Persistence;
namespace NovelGeneratePlugin.Infrastructure.Credentials;

/// <summary>
/// 会话秘密仅存在于内存；持久化使用 Windows 当前用户 DPAPI，并把连接身份、授权代数、端点作为附加熵。
/// 这隔离本插件的连接误用，不声称能对抗同一 Windows 用户权限下的其他进程。
/// </summary>
public sealed class UserCredentialVault(WorkspacePaths paths) : ICredentialVault, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<CredentialTarget, byte[]> _session = [];
    private bool _disposed;
    // 文件和内存索引都包含授权代数。跨进程的旧操作至多修改自己的旧槽位，不能破坏新版认证。
    private string PathFor(CredentialTarget target) => Path.Combine(paths.Root, "Credentials", $"{target.ConnectionId:N}-{target.Epoch:N}.bin");
    private static byte[] Entropy(CredentialTarget target) => SHA256.HashData(Encoding.UTF8.GetBytes($"novel-key-v1|{target.ConnectionId:N}|{target.Epoch:N}|{target.Endpoint}"));
    private static void Validate(CredentialTarget target)
    {
        if (target.ConnectionId == Guid.Empty || target.Epoch == Guid.Empty || string.IsNullOrWhiteSpace(target.Endpoint)) throw new InvalidDataException("凭据授权目标无效。");
    }
    public CredentialState State(CredentialTarget target)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Validate(target);
            if (_session.ContainsKey(target)) return CredentialState.Session;
            try
            {
                var bytes = ReadEncrypted(target);
                if (bytes is null) return CredentialState.Missing;
                CryptographicOperations.ZeroMemory(bytes); return CredentialState.Encrypted;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException) { return CredentialState.Unavailable; }
        }
    }
    public void Set(CredentialTarget target, string secret, bool persist)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192 || secret.Any(char.IsControl)) throw new InvalidDataException("密钥为空、过长或含控制字符。");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Validate(target); var bytes = Encoding.UTF8.GetBytes(secret);
            try
            {
                if (persist)
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("持久密钥仅支持 Windows 用户加密；可选择仅本次会话。");
                    var encrypted = ProtectedData.Protect(bytes, Entropy(target), DataProtectionScope.CurrentUser);
                    var path = PathFor(target); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted); stream.Flush(true); }
                        File.Move(temporary, path, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else DeleteEncrypted(target);
                RemoveSession(target);
                if (!persist) _session[target] = bytes.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void Clear(CredentialTarget target)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Validate(target);
            // 文件删除失败时不能报告清除成功，保留可重试状态。
            DeleteEncrypted(target); RemoveSession(target);
        }
    }
    private void DeleteEncrypted(CredentialTarget target) { try { File.Delete(PathFor(target)); } catch (DirectoryNotFoundException) { } }
    private void RemoveSession(CredentialTarget target)
    {
        if (_session.Remove(target, out var bytes)) CryptographicOperations.ZeroMemory(bytes);
    }
    private byte[]? ReadEncrypted(CredentialTarget target)
    {
        byte[] encrypted;
        try { encrypted = File.ReadAllBytes(PathFor(target)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(encrypted, Entropy(target), DataProtectionScope.CurrentUser);
    }
    public string ReadForRequest(CredentialTarget target)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); Validate(target);
            if (_session.TryGetValue(target, out var session)) return Encoding.UTF8.GetString(session);
            var bytes = ReadEncrypted(target) ?? throw new InvalidOperationException("此连接尚未配置密钥。");
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; foreach (var bytes in _session.Values) CryptographicOperations.ZeroMemory(bytes); _session.Clear(); _disposed = true; }
    }
}
