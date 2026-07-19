using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed record ProjectIdReservationOutcome(string ProjectPath, string ProjectName, ContentIdOccupancyReport Occupancy, ContentIdReservation Reservation)
{
    public uint SingleId => Reservation.Values.Count == 1 ? Reservation.Values[0] : throw new InvalidOperationException("The reservation does not contain exactly one ID.");
}

internal static class ProjectIdReservationBridge
{
    public static async Task<ProjectIdReservationOutcome> ReserveNextAsync(DesktopWorkspaceSession session, ContentIdDomain domain, string purpose,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, IReadOnlyCollection<uint>>? stagedDbcIds = null)
    {
        var projectPath = session.Settings.ActiveProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath)) throw new InvalidOperationException("No active Crucible project is configured. Open Projects & shared IDs first.");
        var project = CrucibleContentProjectService.Load(projectPath); var policy = ContentIdDomainCatalog.Get(domain);
        if (policy.Sources.Any(source => source.Kind == "SQL") && (!session.DatabaseTested || session.DatabaseProfile is null || session.DatabaseCapabilities is null))
            throw new InvalidOperationException("The selected ID domain requires a verified Server & SQL connection.");
        var occupancy = await new ContentIdOccupancyService().InspectAsync(domain, session.DatabaseProfile, session.DatabaseCapabilities,
            session.Settings.CoreDbcPath, session.Settings.SchemaDefinitionPath, cancellationToken: cancellationToken, inMemoryDbcIds: stagedDbcIds);
        var result = CrucibleContentProjectService.ReserveVerifiedIds(projectPath, occupancy, 1, null, purpose);
        return new(Path.GetFullPath(projectPath), project.Name, occupancy, result.Reservation);
    }
}
