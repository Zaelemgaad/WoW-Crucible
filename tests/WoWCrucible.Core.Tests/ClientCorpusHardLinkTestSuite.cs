using System.Text.Json;
using WoWCrucible.Core;

internal static class ClientCorpusHardLinkTestSuite
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Path.Combine(Path.GetTempPath(), $"crucible-client-hardlinks-{Guid.NewGuid():N}");
        var clientsRoot = Path.Combine(fixture, "clients");
        var outputRoot = Path.Combine(fixture, "reports");
        var roots = Enumerable.Range(1, 4).Select(index => Path.Combine(clientsRoot, $"client-{index}")).ToArray();
        try
        {
            foreach (var root in roots)
            {
                Directory.CreateDirectory(Path.Combine(root, "Data"));
                Directory.CreateDirectory(Path.Combine(root, "Cache"));
                File.WriteAllText(Path.Combine(root, "Wow.exe"), "fixture executable");
                File.WriteAllText(Path.Combine(root, ".build.info"), "Version!STRING:0|Active!DEC:1\n3.3.5.12340|1\n");
            }

            var unanimous = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
            var unanimousPaths = new[]
            {
                Path.Combine(roots[0], "Data", "common.MPQ"),
                Path.Combine(roots[1], "Data", "common.MPQ"),
                Path.Combine(roots[2], "Data", "renamed-foundation.MPQ"),
                Path.Combine(roots[3], "foundation.bin")
            };
            foreach (var path in unanimousPaths) File.WriteAllBytes(path, unanimous);
            File.WriteAllText(unanimousPaths[1] + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            File.WriteAllBytes(Path.Combine(roots[0], "Data", "partial.bin"), [1, 2, 3, 4, 5, 6]);
            File.WriteAllBytes(Path.Combine(roots[1], "Data", "partial.bin"), [1, 2, 3, 4, 5, 6]);
            File.WriteAllBytes(Path.Combine(roots[2], "Data", "partial.bin"), [1, 2, 3, 4, 5, 6]);
            File.WriteAllBytes(Path.Combine(roots[3], "Data", "partial.bin"), [6, 5, 4, 3, 2, 1]);
            foreach (var root in roots) File.WriteAllBytes(Path.Combine(root, "Data", "named-stream.bin"), new byte[257]);
            File.WriteAllText(Path.Combine(roots[3], "Data", "named-stream.bin") + ":crucible-metadata", "must-not-be-discarded");
            foreach (var root in roots) File.WriteAllBytes(Path.Combine(root, "Cache", "mutable.bin"), unanimous);

            NativeHardLinks.ReplaceWithHardLink(unanimousPaths[3], unanimousPaths[0]);
            var request = new ClientCorpusHardLinkRequest(2, outputRoot, [clientsRoot],
                [new("wrath-12340", 12340, roots)], ["Cache"], ["Wow.exe", ".build.info"], 1, 2, 1);
            var service = new ClientCorpusHardLinkService();

            var omittedRoot = Path.Combine(clientsRoot, "Cache", "client-5-omitted");
            Directory.CreateDirectory(Path.Combine(omittedRoot, "Data"));
            File.WriteAllText(Path.Combine(omittedRoot, "Wow.exe"), "fixture executable");
            File.WriteAllText(Path.Combine(omittedRoot, ".build.info"), "Version!STRING:0|Active!DEC:1\n3.3.5.12340|1\n");
            File.WriteAllBytes(Path.Combine(omittedRoot, "Data", "foundation.bin"), unanimous);
            try
            {
                _ = service.Plan(request);
                throw new InvalidOperationException("An omitted complete same-build client did not block planning.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("unlisted same-build clients", StringComparison.Ordinal)) { }
            Directory.Delete(omittedRoot, true);

            var plan = service.Plan(request);
            var group = plan.ConsensusGroups.Single(value => value.Sha256 == Hash(unanimousPaths[0]));
            if (!plan.Ready || plan.FormatVersion != ClientCorpusHardLinkService.PlanFormatVersion ||
                plan.Coverage is null || group.ClientCount != 4 || group.Occurrences != 4 || group.ExistingPhysicalFiles != 3 ||
                plan.Actions.Count != 2 || plan.IgnoredZoneIdentifierFiles != 1 || plan.SkippedOtherAlternateStreamFiles != 1 ||
                plan.NearConsensusGroups.All(value => value.PresentClients != 3 || value.RequiredClients != 4) ||
                plan.Actions.Any(action => action.TargetPath.Contains($"{Path.DirectorySeparatorChar}Cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Cohort consensus did not require all four clients, preserve renamed byte matches, or honor mutable-directory exclusions.");

            var tamperedPath = Path.Combine(outputRoot, "tampered-coverage-plan.json");
            var tampered = plan with
            {
                Coverage = plan.Coverage with { DiscoveryRoots = [roots[0]] }
            };
            File.WriteAllText(tamperedPath, JsonSerializer.Serialize(tampered));
            try
            {
                _ = ClientCorpusHardLinkService.LoadPlan(tamperedPath);
                throw new InvalidOperationException("Tampered whole-library coverage metadata did not invalidate the plan.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)) { }

            var legacyPath = Path.Combine(outputRoot, "legacy-unbound-plan.json");
            var legacy = plan with
            {
                FormatVersion = 1,
                Fingerprint = ClientCorpusHardLinkService.LegacyFingerprint(
                    plan.Roots, plan.ConsensusGroups, plan.Actions)
            };
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));
            _ = ClientCorpusHardLinkService.LoadPlan(legacyPath);
            try
            {
                _ = service.Apply(legacyPath, materialize: false);
                throw new InvalidOperationException("A legacy plan without fingerprint-bound coverage was allowed to create hard links.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("legacy plan", StringComparison.OrdinalIgnoreCase)) { }
            var legacyMaterialize = service.Apply(legacyPath, materialize: true);
            if (!legacyMaterialize.Passed ||
                legacyMaterialize.Entries.Any(entry => entry.State != ClientCorpusHardLinkApplyState.AlreadyMaterialized))
                throw new InvalidOperationException("A legacy plan could not be retained for rollback-only materialization.");

            Directory.CreateDirectory(Path.Combine(omittedRoot, "Data"));
            File.WriteAllText(Path.Combine(omittedRoot, "Wow.exe"), "fixture executable");
            File.WriteAllText(Path.Combine(omittedRoot, ".build.info"), "Version!STRING:0|Active!DEC:1\n3.3.5.12340|1\n");
            File.WriteAllBytes(Path.Combine(omittedRoot, "Data", "foundation.bin"), unanimous);
            try
            {
                _ = service.Apply(plan.PlanPath, materialize: false);
                throw new InvalidOperationException("Post-plan same-build client drift did not block apply.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("coverage changed after planning", StringComparison.Ordinal)) { }
            Directory.Delete(omittedRoot, true);

            var apply = service.Apply(plan.PlanPath, materialize: false);
            if (!apply.Passed || apply.Entries.Count(entry => entry.State == ClientCorpusHardLinkApplyState.Linked) != 2)
                throw new InvalidOperationException("Reviewed unanimous files were not hard-linked.");
            var journalLines = File.ReadAllLines(apply.JournalPath);
            if (journalLines.Length != 4 || journalLines.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("The operation journal is not one complete JSON object per line.");
            foreach (var line in journalLines)
                using (JsonDocument.Parse(line)) { }
            var identity = ClientCorpusHardLinkService.ReadIdentity(unanimousPaths[0]).Key;
            if (unanimousPaths.Any(path => ClientCorpusHardLinkService.ReadIdentity(path).Key != identity))
                throw new InvalidOperationException("Applied unanimous files do not share one NTFS identity.");
            var repeated = service.Apply(plan.PlanPath, materialize: false);
            if (!repeated.Passed || repeated.Entries.Any(entry => entry.State != ClientCorpusHardLinkApplyState.AlreadyLinked))
                throw new InvalidOperationException("Hard-link application is not safely resumable/idempotent.");

            var materialized = service.Apply(plan.PlanPath, materialize: true);
            if (!materialized.Passed || materialized.Entries.Count(entry => entry.State == ClientCorpusHardLinkApplyState.Materialized) != 2 ||
                plan.Actions.Any(action => ClientCorpusHardLinkService.ReadIdentity(action.TargetPath).Key ==
                                           ClientCorpusHardLinkService.ReadIdentity(action.CanonicalPath).Key) ||
                unanimousPaths.Any(path => !File.ReadAllBytes(path).SequenceEqual(unanimous)))
                throw new InvalidOperationException("Materialization did not restore independent byte-identical files.");

            var cloneRoot = Path.Combine(fixture, "consensus-clone");
            var serverSource = Path.Combine(fixture, "server-source");
            var serverClone = Path.Combine(fixture, "server-clone");
            Directory.CreateDirectory(serverSource);
            File.WriteAllText(Path.Combine(serverSource, "worldserver.exe"), "mutable-server-runtime");
            var cloneRequest = new CompatibilityLabRequest(1, Path.Combine(fixture, "clone-reports"),
            [
                new("Client clone", "mop-18414", cloneRoot, cloneRoot, null, "definitions", null,
                    [new("client", roots[0], cloneRoot, ["Cache"], null, plan.PlanPath)]),
                new("Server clone", "legion-26972", serverClone, serverClone, null, "definitions", null,
                    [new("server", serverSource, serverClone)])
            ]);
            var prepared = CompatibilityLabService.PrepareClones(cloneRequest);
            var clientEntry = prepared.Entries[0];
            var clonedUnanimous = Path.Combine(cloneRoot, Path.GetRelativePath(roots[0], unanimousPaths[0]));
            var sourcePartial = Path.Combine(roots[0], "Data", "partial.bin");
            var clonedPartial = Path.Combine(cloneRoot, "Data", "partial.bin");
            if (!prepared.Passed || clientEntry.LinkedFiles < 1 || clientEntry.CopiedFiles < 1 ||
                ClientCorpusHardLinkService.ReadIdentity(clonedUnanimous).Key !=
                ClientCorpusHardLinkService.ReadIdentity(unanimousPaths[0]).Key ||
                ClientCorpusHardLinkService.ReadIdentity(clonedPartial).Key ==
                ClientCorpusHardLinkService.ReadIdentity(sourcePartial).Key)
                throw new InvalidOperationException("Compatibility client cloning did not link unanimous files and independently copy non-consensus files.");
            var verifiedClone = CompatibilityLabService.PrepareClones(cloneRequest);
            if (!verifiedClone.Passed || verifiedClone.Entries[0].State != CompatibilityLabClonePreparationState.VerifiedExisting ||
                verifiedClone.Entries[0].LinkedFiles != 1 || verifiedClone.Entries[0].LinkedBytes != unanimous.Length)
                throw new InvalidOperationException("A completed consensus-backed client clone failed hard-link identity verification or reporting.");

            var stalePlan = service.Plan(request);
            var staleAction = stalePlan.Actions.First();
            var staleBytes = File.ReadAllBytes(staleAction.TargetPath);
            staleBytes[0] ^= 0x7F;
            File.WriteAllBytes(staleAction.TargetPath, staleBytes);
            var rejected = service.Apply(stalePlan.PlanPath, materialize: false);
            if (rejected.Passed || rejected.Entries.Single().State != ClientCorpusHardLinkApplyState.Failed ||
                ClientCorpusHardLinkService.ReadIdentity(staleAction.TargetPath).Key ==
                ClientCorpusHardLinkService.ReadIdentity(staleAction.CanonicalPath).Key)
                throw new InvalidOperationException("A stale hard-link plan was not rejected before mutation.");
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
