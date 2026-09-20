using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WoWCrucible.Core;

internal static class ModelBrowserTestSuite
{
    public static void Run(string? corpus = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "crucible-model-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = Model(); var skin = Skin(); var skeleton = Skeleton();
            File.WriteAllBytes(Path.Combine(root, "Model.m2"), Chunk("MD21", model).Concat(Chunk("SKID", UInt(123))).ToArray());
            File.WriteAllBytes(Path.Combine(root, "Model00.skin"), skin);
            File.WriteAllBytes(Path.Combine(root, "Model.skel"), skeleton);
            var hashes = Directory.GetFiles(root).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
            using (var zip = ZipFile.Open(Path.Combine(root, "models.zip"), ZipArchiveMode.Create))
                foreach (var file in Directory.GetFiles(root).Where(file => !file.EndsWith(".zip")))
                { using var stream = zip.CreateEntry("Character/Test/" + Path.GetFileName(file)).Open(); stream.Write(File.ReadAllBytes(file)); }
            var catalog = ModelBrowserCatalogService.Scan(root);
            Require(catalog.Models.Count == 2 && catalog.Errors.Count == 0, "Loose and ZIP model discovery.");
            foreach (var entry in catalog.Models)
            {
                using var source = new ModelBrowserSource(entry, catalog);
                var geometry = M2PreviewGeometryService.LoadForViewing(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets);
                Require(geometry.SourceVersion == 274 && geometry.Bones.Count == 1 && geometry.Sequences.Count == 1, "External skeleton metadata.");
                Require(geometry.TriangleIndices.Count == 3 && geometry.TextureSlots.Single().Type == 1, "Modern geometry and texture slots.");
                Require(geometry.Cameras.Count == 1, "Model camera globals are independent of skeleton globals.");
                var pose = M2AnimationService.CreatePose(geometry);
                M2AnimationService.SampleInto(geometry, 0, 0, pose); var first = pose.Vertices[0];
                M2AnimationService.SampleInto(geometry, 0, 500, pose);
                Require(Vector3.Distance(first, pose.Vertices[0]) > 0.9f, "SKB1 animation tracks retain their own address space.");
                var hidden = M2PreviewGeometryService.SelectGeosets(geometry, new HashSet<int>());
                var shown = M2PreviewGeometryService.SelectGeosets(geometry, new HashSet<int> { 0 });
                Require(hidden.TriangleIndices.Count == 0 && shown.TriangleIndices.SequenceEqual(geometry.TriangleIndices), "Reversible geoset visibility.");
            }
            foreach (var pair in hashes) Require(pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key))), "Preview mutated a source file.");
            Require(Directory.GetFiles(root).Length == 4, "ZIP preview extracted files.");
            Expect<InvalidDataException>(() => ModelBrowserSource.Normalize("../outside.blp"));
            using (var broken = new ModelBrowserSource(new ModelBrowserEntry(WriteTruncated(root), null, "broken.m2", 8, "MD21", null, null)))
                Expect<InvalidDataException>(() => M2PreviewGeometryService.LoadForViewing(broken));
            var legacy = Model(); U32(legacy, 4, 264); U32(legacy, 0x110, 0);
            for (var index = 0; index < 3; index++) legacy[0x130 + index * 48 + 12] = 0;
            var legacyPath = Path.Combine(root, "Legacy.m2"); File.WriteAllBytes(legacyPath, legacy);
            File.WriteAllBytes(Path.Combine(root, "Legacy00.skin"), skin);
            Require(M2PreviewGeometryService.Load(legacyPath).TriangleIndices.Count == 3, "Existing Wrath geometry loader.");
            var largePath = Path.Combine(root, "Large.m2"); File.WriteAllBytes(largePath, legacy);
            File.WriteAllBytes(Path.Combine(root, "Large00.skin"), LargeSkin());
            Require(M2PreviewGeometryService.Load(largePath).TriangleIndices.SequenceEqual(new[] { 0, 1, 2 }), "HD section triangle offsets retain their high 16 bits.");
            CheckParentSkeleton(root);
            CheckViewerDefaults();
            CheckTextureChoices();
            CheckCamera();
            Require(M2AnimationNames.Get(0) == "Stand" && M2AnimationNames.Get(5) == "Run", "Named basic animations.");
            Require(M2AnimationNames.Get(811) == "Fly Combat Ability 2H Big 01", "Extended animation names are available offline.");
            Require(M2AnimationNames.Get(ushort.MaxValue).StartsWith("Unknown animation"), "Unknown animation IDs are not mislabelled.");
            var beforeCancel = new CancellationToken(true);
            Expect<OperationCanceledException>(() => ModelBrowserCatalogService.Scan(root, beforeCancel));
            Console.WriteLine("PASS model browser: folder/ZIP discovery, read-only preview, external skeleton animation, unarmored face/body/ear defaults, camera target/model movement, animation names, malformed input, traversal, cancellation.");
            if (corpus is not null) AuditCorpus(corpus);
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (Path.GetDirectoryName(absolute) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(absolute).StartsWith("crucible-model-browser-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unexpected fixture cleanup path.");
            Directory.Delete(absolute, true);
        }
    }

    private static void AuditCorpus(string root)
    {
        var catalog = ModelBrowserCatalogService.Scan(root); var loaded = 0; var animated = 0; var failed = 0;
        foreach (var entry in catalog.Models)
        {
            using var source = new ModelBrowserSource(entry, catalog);
            try
            {
                var geometry = M2PreviewGeometryService.LoadForViewing(source, visibilityMode: M2PreviewVisibilityMode.AllGeosets);
                loaded++;
                if (geometry.Sequences.Count > 0)
                {
                    var pose = M2AnimationService.CreatePose(geometry);
                    M2AnimationService.SampleInto(geometry, 0, 500, pose); animated++;
                }
            }
            catch (Exception exception) { failed++; Console.WriteLine($"REVIEW {entry.RelativePath}: {exception.Message}"); }
        }
        Console.WriteLine($"CORPUS {catalog.Models.Count} models; {loaded} geometry loaded; {animated} animation sampled; {failed} load/animation failures; {catalog.UnopenedArchives.Count} unopened archives; {catalog.Errors.Count} scan errors.");
        Require(loaded > 0 && animated > 0, "No real corpus models could be viewed/animated.");
    }

    private static void CheckViewerDefaults()
    {
        var sections = new List<M2PreviewSubmesh> { Section(0, 0, 100), Section(1, 3201, 112), Section(2, 3202, 114), Section(3, 3202, 1780),
            Section(4, 3203, 114), Section(5, 3203, 1780), Section(6, 702, 204), Section(7, 703, 204), Section(8, 3501, 500), Section(9, 3601, 500),
            Section(10, 401, 240), Section(11, 2001, 1430), Section(12, 2301, 120), Section(13, 3301, 256), Section(14, 3401, 128) };
        // Include both absent-01 armor groups and arbitrary customization groups.
        foreach (var id in new ushort[] { 1, 2, 402, 2002, 2302, 3302, 3402, 502, 802, 902, 1502, 704, 5101 })
            sections.Add(Section(sections.Count, id, 300));
        foreach (var group in Enumerable.Range(1, 43).Except(new[] { 4, 7, 20, 23, 32, 33, 34, 35, 36 }))
            sections.Add(Section(sections.Count, (ushort)(group * 100 + 1), 300));
        var defaults = M2GeosetCatalog.BrowserDefaults(sections);
        Require(defaults.SetEquals(new HashSet<int> { 0, 1, 2, 3, 6, 7, 10, 11, 12, 13, 14 }), "Only base body, first hands/bare feet/hand attachments, two ears, eyes/brows, full paired face and neck start visible.");
        Require(M2GeosetCatalog.BrowserDefaults(sections.Where(s => s.GeosetId != 3201).ToArray()).SetEquals(new HashSet<int> { 0, 2, 3, 6, 7, 10, 11, 12, 13, 14 }), "Missing neck does not select two faces or substitute armor.");
        Require(M2GeosetCatalog.BrowserDefaults(sections.Where(s => s.GeosetGroup != 7 || s.GeosetId == 703).ToArray()).Contains(7), "A model with only one available ear variant still shows it.");
        static M2PreviewSubmesh Section(int index, ushort id, int triangles) => new(index, id, 0, 0, 0, 0, triangles * 3, true);
    }

    private static void CheckCamera()
    {
        var camera = new ModelPreviewCamera(); camera.Frame(new(-1, -1, -2), new(1, 1, 2));
        camera.PanTarget(70, -50, 800, 600, Matrix4x4.Identity); var target = camera.State.Target;
        camera.Orbit(30, 25); camera.Zoom(8);
        Require(camera.State.Target == target, "Orbit and zoom preserve the user's chosen target.");
        camera.MoveModel(20, 10, 800, 600, Matrix4x4.Identity);
        Require(camera.State.Target == target && camera.State.ModelOffset != Vector3.Zero, "Model dragging is independent of camera target.");
        var offset = camera.State.ModelOffset;
        camera.Focus(new(-0.2f, -0.2f, 1.5f), new(0.2f, 0.2f, 2));
        Require(camera.State.ModelOffset == offset && camera.State.Zoom > 5 && camera.State.Target == new Vector3(0, 0, 1.75f) + offset, "Face framing keeps model placement and zooms to the face bounds.");
    }

    private static void CheckTextureChoices()
    {
        const string local = "PackA/Character/Human/Female/";
        var body = local + "HumanFemaleSkin00_00.blp";
        var otherBody = "PackB/Character/Human/Female/HumanFemaleSkin00_00.blp";
        var modernBody = local + "humanfemale_hd_skin_color_123.blp";
        var hair = local + "HumanFemaleHair00_00.blp";
        var upper = local + "HumanFemaleFaceUpper00_00.blp";
        var lower = local + "HumanFemaleFaceLower00_00.blp";
        var eyes = local + "humanfemale_eye_color_456.blp";
        string[] paths = [body, otherBody, modernBody, hair, upper, lower, eyes,
            local + "HumanFemaleNakedTorsoSkin00_00.blp", local + "ScalpUpperHair00_00.blp", local + "HumanFemaleSkin00_00_normal.blp",
            local + "humanfemale_hd_skin_normal_124.blp", local + "HumanFemaleSkin00_00_extra.blp", local + "HumanFemaleFacialHair00_00.blp",
            local + "HumanFemale_Jewelry_Color_22.blp", local + "HumanFemaleEyebrow00.blp", local + "HumanFemaleTattoo00.blp",
            "Other/Character/Orc/Female/OrcFemaleSkin00_00.blp", "Other/Character/Nightborne/Female/NightborneHair00_00.blp",
            "Item/ObjectComponents/Cape/Cloak01.blp", "Item/ObjectComponents/Weapon/Sword01.blp",
            "Item/ObjectComponents/Weapon/Sword_blade01.blp", "Item/ObjectComponents/Weapon/Sword_handle01.blp",
            "Creature/Wolf/WolfSkinBlack.blp", "Creature/Wolf/Fire.blp", "Creature/Lion/Lion_mane01.blp",
            "Textures/Reflect.blp", "Textures/GuildEmblems/Background_01.blp", "Textures/GuildEmblems/Border_01.blp",
            "Textures/GuildEmblems/Emblem_01.blp", "Textures/GuildEmblems/EmblemColor_01.blp", "Interface/Icons/INV_Sword_01.blp",
            "World/Furniture/Chair00.blp", "World/Landscape.blp", "World/Permanent01.blp", "Unknown/987654.blp",
            "Effects/Spark01.blp", "Effects/Spark02.blp", "Unknown/654321.blp", "NotATexture/HumanFemaleSkin00_00.txt"];
        var slots = Enumerable.Range(1, 20).Select(type => new M2TextureSlot(type, (uint)type, 0, null)).Concat(new[]
        {
            new M2TextureSlot(21, 0, 0, "Character\\Human\\Female\\HumanFemaleFaceUpper00_00.blp"),
            new M2TextureSlot(22, 0, 0, null) { FileDataId = 987654 },
            new M2TextureSlot(23, 0, 0, "Effects/Spark01.blp"), new M2TextureSlot(24, 0, 0, null),
            new M2TextureSlot(25, 0, 0, null) { FileDataId = 123 }
        }).ToArray();
        var geometry = new M2PreviewGeometry("E:/Collection/" + local + "HumanFemale_HD.m2", "fixture.skin", [], [], [], [], Vector3.Zero, Vector3.One, slots);
        var choices = ModelBrowserTextureService.BuildChoices(geometry, paths.Concat([body.ToUpperInvariant()]), includeOtherModels: true);
        Require(choices[1].ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([body, otherBody, modernBody, "Other/Character/Orc/Female/OrcFemaleSkin00_00.blp"]), "Body choices exclude hair, partial face/underwear overlays, normal maps, extra skin pieces and other materials.");
        var bodyChoices = choices[1].ToList();
        Require(bodyChoices.IndexOf(body) < bodyChoices.IndexOf(otherBody) && bodyChoices.IndexOf(otherBody) < bodyChoices.IndexOf("Other/Character/Orc/Female/OrcFemaleSkin00_00.blp"), "Same-folder and same-model body matches sort before unrelated models; alternate pack copies stay distinct.");
        Require(choices[6].ToHashSet().SetEquals([hair, "Other/Character/Nightborne/Female/NightborneHair00_00.blp"]), "Hair choices exclude scalp overlays, facial hair, body atlases and chair textures.");
        Require(choices[19].SequenceEqual([eyes]), "Eye choices exclude skin and brows.");
        Require(choices[21].SequenceEqual([upper]), "Embedded face-upper reference does not admit full bodies or lower-face overlays.");
        Require(choices[22].SequenceEqual(["Unknown/987654.blp"]) && choices[24].Count == 0, "Opaque FileDataID is matched exactly; unknown material is not a catch-all.");
        Require(choices[23].ToHashSet().SetEquals(["Effects/Spark01.blp", "Effects/Spark02.blp"]), "Literal model texture families remain available without admitting arbitrary effects.");
        Require(choices[25].Contains(modernBody) && choices[25].Contains(otherBody) && !choices[25].Contains(hair), "A named FileDataID match establishes the role of an otherwise unnamed slot.");
        foreach (var (slot, expected) in new (int, string)[] { (3, "Item/ObjectComponents/Weapon/Sword_blade01.blp"), (4, "Item/ObjectComponents/Weapon/Sword_handle01.blp"),
            (5, "Textures/Reflect.blp"), (7, local + "HumanFemaleFacialHair00_00.blp"), (9, "Interface/Icons/INV_Sword_01.blp"),
            (10, "Creature/Lion/Lion_mane01.blp"), (11, "Creature/Wolf/WolfSkinBlack.blp"), (12, "Creature/Wolf/WolfSkinBlack.blp"), (13, "Creature/Wolf/WolfSkinBlack.blp"),
            (14, "Interface/Icons/INV_Sword_01.blp"), (15, "Textures/GuildEmblems/Background_01.blp"), (16, "Textures/GuildEmblems/EmblemColor_01.blp"),
            (17, "Textures/GuildEmblems/Border_01.blp"), (18, "Textures/GuildEmblems/Emblem_01.blp"), (20, local + "HumanFemale_Jewelry_Color_22.blp") })
            Require(choices[slot].SequenceEqual([expected]), $"Material {slot} contains unrelated textures.");
        Require(choices[2].Count == 4 && choices[2].Contains("Item/ObjectComponents/Cape/Cloak01.blp") && !choices[2].Contains("World/Landscape.blp"), "Item/cape choices contain equipment, not scenery.");
        Require(choices[8].Contains(upper) && choices[8].Contains(lower) && !choices[8].Contains(body), "Skin-detail choices are separate from full-body atlases.");
        var localChoices = ModelBrowserTextureService.BuildChoices(geometry, paths);
        Require(localChoices[1].ToHashSet().SetEquals([body, otherBody, modernBody]) && localChoices[6].SequenceEqual([hair]), "Default choices retain matching character textures across packs, excluding other races/sexes.");
        Require(localChoices[5].SequenceEqual(choices[5]) && localChoices[2].SequenceEqual(choices[2]), "Shared reflection and equipment textures remain available without character ownership.");
        Console.WriteLine("PASS role-specific texture choices: body/hair/eyes/overlays/equipment/guild/effects, exact file IDs, duplicate pack paths and local-first ordering.");
    }

    private static string WriteTruncated(string root)
    {
        var path = Path.Combine(root, "broken.m2"); File.WriteAllBytes(path, [77, 68, 50, 49, 255, 255, 255, 127]); return path;
    }
    private static byte[] Model()
    {
        var data = new byte[0x1D6 + 8 + 116]; Encoding.ASCII.GetBytes("MD20").CopyTo(data, 0);
        U32(data, 4, 274); U32(data, 0x10, 0x80); U32(data, 0x3C, 3); U32(data, 0x40, 0x130); U32(data, 0x44, 1);
        for (var index = 0; index < 3; index++)
        {
            var offset = 0x130 + index * 48; Float(data, offset, index == 1 ? 1 : 0); Float(data, offset + 8, index == 2 ? 1 : 0);
            data[offset + 12] = 255; Float(data, offset + 24, 1);
        }
        U32(data, 0x50, 1); U32(data, 0x54, 0x1C0); U32(data, 0x1C0, 1);
        U32(data, 0x70, 1); U32(data, 0x74, 0x1D0); U32(data, 0x80, 1); U32(data, 0x84, 0x1D4);
        U32(data, 0x14, 2); U32(data, 0x18, 0x1D6); U32(data, 0x1D6, 1000); U32(data, 0x1DA, 1500);
        const int camera = 0x1DE;
        U32(data, 0x110, 1); U32(data, 0x114, camera);
        Float(data, camera + 4, 10000); Float(data, camera + 8, 0.1f); Float(data, camera + 32, 10);
        U16(data, camera + 14, ushort.MaxValue); U16(data, camera + 46, 1); U16(data, camera + 78, ushort.MaxValue);
        return data;
    }
    private static byte[] Skin()
    {
        var data = new byte[56 + 6 + 6 + 48 + 24]; Encoding.ASCII.GetBytes("SKIN").CopyTo(data, 0);
        U32(data, 4, 3); U32(data, 8, 56); U32(data, 12, 3); U32(data, 16, 62);
        for (ushort index = 0; index < 3; index++) { U16(data, 56 + index * 2, index); U16(data, 62 + index * 2, index); }
        U32(data, 28, 1); U32(data, 32, 68); U16(data, 68 + 6, 3); U16(data, 68 + 10, 3);
        U32(data, 36, 1); U32(data, 40, 116); U16(data, 116 + 14, 1); return data;
    }
    private static byte[] Skeleton(bool globals = true)
    {
        var sequences = new byte[24 + 64 + 8]; U32(sequences, 8, 1); U32(sequences, 12, 24); U32(sequences, 28, 1000); U32(sequences, 36, 0x20);
        U32(sequences, 0, globals ? 2u : 0u); U32(sequences, 4, 88); U32(sequences, 88, 1000); U32(sequences, 92, 1500);
        var bones = new byte[200]; U32(bones, 0, 1); U32(bones, 4, 16); U16(bones, 24, ushort.MaxValue);
        foreach (var track in new[] { 32, 52, 72 }) U16(bones, track + 2, ushort.MaxValue);
        U16(bones, 32, 1); U32(bones, 36, 1); U32(bones, 40, 144); U32(bones, 44, 1); U32(bones, 48, 152);
        U32(bones, 144, 2); U32(bones, 148, 160); U32(bones, 152, 2); U32(bones, 156, 168);
        U32(bones, 164, 1000); Float(bones, 180, 2);
        return Chunk("SKS1", sequences).Concat(Chunk("SKB1", bones)).ToArray();
    }
    private static void CheckParentSkeleton(string root)
    {
        var sequences = new byte[160]; U32(sequences, 0, 2); U32(sequences, 4, 152); U32(sequences, 8, 2); U32(sequences, 12, 24);
        U32(sequences, 152, 1000); U32(sequences, 156, 1500);
        U16(sequences, 24, 77); U32(sequences, 28, 1000); U32(sequences, 36, 0x20);
        U32(sequences, 92, 1000); U32(sequences, 100, 0x20);
        var parent = new byte[16]; U32(parent, 8, 123);
        File.WriteAllBytes(Path.Combine(root, "Child.skel"), Chunk("SKS1", sequences).Concat(Chunk("SKPD", parent)).ToArray());
        File.WriteAllBytes(Path.Combine(root, "123.skel"), Skeleton(false));
        var model = Model(); var payload = model.Length; Array.Resize(ref model, payload + 48);
        const int positionTrack = 0x1DE + 12;
        U32(model, positionTrack + 4, 2); U32(model, positionTrack + 8, (uint)payload);
        U32(model, positionTrack + 12, 2); U32(model, positionTrack + 16, (uint)payload + 16);
        U32(model, payload + 8, 1); U32(model, payload + 12, (uint)payload + 32);
        U32(model, payload + 24, 1); U32(model, payload + 28, (uint)payload + 36); Float(model, payload + 36, 5);
        var path = Path.Combine(root, "Child.m2");
        File.WriteAllBytes(path, Chunk("MD21", model).Concat(Chunk("SKID", UInt(456))).ToArray());
        File.WriteAllBytes(Path.Combine(root, "Child00.skin"), Skin());
        using var source = new ModelBrowserSource(new(path, null, "Child.m2", new FileInfo(path).Length, "MD21", 274, null));
        var geometry = M2PreviewGeometryService.LoadForViewing(source);
        var pose = M2AnimationService.CreatePose(geometry); M2AnimationService.SampleInto(geometry, 0, 0, pose);
        Require(Math.Abs(pose.Cameras[0].Position.X - 15) < 0.001f, "Camera tracks use the child's sequence index and globals while bones use the parent skeleton.");
    }
    private static byte[] LargeSkin()
    {
        const int triangles = 65541;
        const int section = 62 + triangles * 2;
        var data = new byte[section + 48 + 24]; Encoding.ASCII.GetBytes("SKIN").CopyTo(data, 0);
        U32(data, 4, 3); U32(data, 8, 56); U32(data, 12, triangles); U32(data, 16, 62);
        for (ushort index = 0; index < 3; index++) { U16(data, 56 + index * 2, index); U16(data, 62 + (65538 + index) * 2, index); }
        U32(data, 28, 1); U32(data, 32, section); U16(data, section + 2, 1); U16(data, section + 6, 3); U16(data, section + 8, 2); U16(data, section + 10, 3);
        U32(data, 36, 1); U32(data, 40, section + 48); U16(data, section + 48 + 14, 1); return data;
    }
    private static byte[] Chunk(string name, byte[] data) => Encoding.ASCII.GetBytes(name).Concat(UInt((uint)data.Length)).Concat(data).ToArray();
    private static byte[] UInt(uint value) { var data = new byte[4]; U32(data, 0, value); return data; }
    private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
