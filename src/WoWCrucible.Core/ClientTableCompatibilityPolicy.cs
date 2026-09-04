namespace WoWCrucible.Core;

public enum ClientTableCompatibilityState
{
    Supported,
    UnsupportedExtension,
    UnsupportedContainer,
    InvalidContainer,
    EmptyUnverifiable
}

public sealed record ClientTableCompatibilityAssessment(
    string Path,
    string Table,
    ClientTableCompatibilityState State,
    ClientTableContainerKind? Container,
    string Message)
{
    public bool CanTarget => State is ClientTableCompatibilityState.Supported or ClientTableCompatibilityState.EmptyUnverifiable;
}

/// <summary>
/// Owns the build-profile boundary for client tables. Extensions identify table
/// paths; only the binary signature proves the actual container.
/// </summary>
public static class ClientTableCompatibilityPolicy
{
    public static bool IsTableExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dbc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".db2", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClientTablePath(string archivePath) =>
        archivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && IsTableExtension(archivePath);

    public static string ArchivePathFor(string filePath) => $"DBFilesClient\\{Path.GetFileName(filePath)}";

    public static ClientTableCompatibilityAssessment Assess(string path, TargetProfile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        path = Path.GetFullPath(path);
        var table = Path.GetFileNameWithoutExtension(path);
        if (!IsTableExtension(path))
            return new(path, table, ClientTableCompatibilityState.UnsupportedExtension, null,
                $"{Path.GetExtension(path)} is not a supported client-table extension.");
        if (!File.Exists(path)) throw new FileNotFoundException("Client table does not exist.", path);

        if (new FileInfo(path).Length == 0)
        {
            var extensionAllowed = Path.GetExtension(path).Equals(".dbc", StringComparison.OrdinalIgnoreCase)
                ? target.SupportsWdbc
                : target.SupportsDb2 || target.SupportsWdc1;
            return extensionAllowed
                ? new(path, table, ClientTableCompatibilityState.EmptyUnverifiable, null,
                    $"The zero-byte placeholder has no binary signature; its extension is permitted by {target.DisplayName}, but no container is claimed.")
                : new(path, table, ClientTableCompatibilityState.UnsupportedExtension, null,
                    $"{target.DisplayName} does not permit {Path.GetExtension(path)} client tables.");
        }

        try
        {
            var file = WdbcFile.Load(path);
            if (!Supports(target, file.ContainerKind))
                return new(path, table, ClientTableCompatibilityState.UnsupportedContainer, file.ContainerKind,
                    $"{file.ContainerKind} is not supported by {target.DisplayName} ({target.TableFormats}).");
            if (file.ContainerKind == ClientTableContainerKind.Wdb2)
            {
                var embeddedBuild = file.Db2Metadata?.Build;
                if (embeddedBuild is null || !target.AcceptsEmbeddedTableBuild(embeddedBuild.Value))
                    return new(path, table, ClientTableCompatibilityState.UnsupportedContainer, file.ContainerKind,
                        $"WDB2 build {embeddedBuild?.ToString("N0") ?? "unknown"} cannot target {target.DisplayName}. Accepted embedded builds: {string.Join(", ", new[] { target.ClientBuild }.Concat(target.CompatibleEmbeddedTableBuilds).Distinct().Order().Select(build => build.ToString("N0")))}.");
            }
            return new(path, table, ClientTableCompatibilityState.Supported, file.ContainerKind,
                $"{file.ContainerKind} is supported by {target.DisplayName}.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            return new(path, table, ClientTableCompatibilityState.InvalidContainer, null, exception.Message);
        }
    }

    public static bool Supports(TargetProfile target, ClientTableContainerKind container) => container switch
    {
        ClientTableContainerKind.Wdbc => target.SupportsWdbc,
        ClientTableContainerKind.Wdb2 => target.SupportsDb2,
        ClientTableContainerKind.Wdc1 => target.SupportsWdc1,
        _ => false
    };

    public static bool HasSameLayout(WdbcFile left, WdbcFile right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.ContainerKind != right.ContainerKind || left.FieldCount != right.FieldCount || left.RecordSize != right.RecordSize) return false;
        if (left.ContainerKind == ClientTableContainerKind.Wdb2)
            return left.Db2Metadata is { } leftDb2 && right.Db2Metadata is { } rightDb2 &&
                   leftDb2.Build == rightDb2.Build && leftDb2.TableHash == rightDb2.TableHash;
        if (left.ContainerKind == ClientTableContainerKind.Wdc1)
            return left.Wdc1Metadata is { } leftWdc && right.Wdc1Metadata is { } rightWdc &&
                   leftWdc.TableHash == rightWdc.TableHash && leftWdc.LayoutHash == rightWdc.LayoutHash;
        return true;
    }
}
