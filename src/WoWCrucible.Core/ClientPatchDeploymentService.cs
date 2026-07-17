using System.Security.Cryptography;

namespace WoWCrucible.Core;

public sealed record ClientCacheInvalidationResult(string CachePath, bool Existed, long DeletedBytes, int DeletedFiles);

public sealed record ClientPatchDeploymentResult(
    string SourcePath,
    string InstalledPath,
    string? BackupPath,
    string Sha256,
    ClientCacheInvalidationResult Cache);

public static class ClientPatchDeploymentService
{
    public static ClientPatchDeploymentResult Install(string sourcePatch, string clientRoot, string? targetFileName = null)
    {
        sourcePatch = Path.GetFullPath(sourcePatch);
        if (!File.Exists(sourcePatch)) throw new FileNotFoundException("The patch MPQ does not exist.", sourcePatch);
        if (!Path.GetExtension(sourcePatch).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Client patch deployment accepts only an MPQ file.");

        clientRoot = ValidateClientRoot(clientRoot);
        var dataPath = Path.Combine(clientRoot, "Data");
        targetFileName = string.IsNullOrWhiteSpace(targetFileName) ? Path.GetFileName(sourcePatch) : Path.GetFileName(targetFileName);
        if (!Path.GetExtension(targetFileName).Equals(".mpq", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed patch name must end in .MPQ.");

        var targetPath = Path.GetFullPath(Path.Combine(dataPath, targetFileName));
        if (!string.Equals(Path.GetDirectoryName(targetPath), dataPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installed patch path escaped the client Data folder.");
        if (sourcePatch.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected patch is already installed at that path. Update it in place, then clear the client cache.");

        var temporaryPath = Path.Combine(dataPath, $".{targetFileName}.{Guid.NewGuid():N}.crucible.tmp");
        string? backupPath = null;
        try
        {
            File.Copy(sourcePatch, temporaryPath, false);
            var sourceHash = Sha256(sourcePatch);
            var copiedHash = Sha256(temporaryPath);
            if (!sourceHash.Equals(copiedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The copied patch failed SHA-256 verification; the installed client was not changed.");

            if (File.Exists(targetPath))
            {
                backupPath = targetPath + ".bak";
                File.Copy(targetPath, backupPath, true);
            }
            File.Move(temporaryPath, targetPath, true);
            var cache = InvalidateCache(clientRoot);
            return new(sourcePatch, targetPath, backupPath, sourceHash, cache);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static ClientCacheInvalidationResult InvalidateCache(string clientRoot)
    {
        clientRoot = ValidateClientRoot(clientRoot);
        var cachePath = Path.GetFullPath(Path.Combine(clientRoot, "Cache"));
        if (!string.Equals(Path.GetDirectoryName(cachePath), clientRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(cachePath).Equals("Cache", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing to invalidate a cache path that is not the selected client's exact Cache folder.");
        if (!Directory.Exists(cachePath)) return new(cachePath, false, 0, 0);

        var files = Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).ToArray();
        var bytes = files.Sum(file => file.Length);
        Directory.Delete(cachePath, true);
        return new(cachePath, true, bytes, files.Length);
    }

    public static bool IsInsideClientData(string path, string clientDataPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(clientDataPath)) return false;
        var fullPath = Path.GetFullPath(path);
        var dataPath = Path.GetFullPath(clientDataPath);
        return string.Equals(Path.GetDirectoryName(fullPath), dataPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateClientRoot(string clientRoot)
    {
        clientRoot = Path.GetFullPath(clientRoot);
        if (!Directory.Exists(clientRoot)) throw new DirectoryNotFoundException($"Client root does not exist: {clientRoot}");
        if (!Directory.Exists(Path.Combine(clientRoot, "Data")))
            throw new DirectoryNotFoundException($"The selected client root has no Data folder: {clientRoot}");
        return Path.TrimEndingDirectorySeparator(clientRoot);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
