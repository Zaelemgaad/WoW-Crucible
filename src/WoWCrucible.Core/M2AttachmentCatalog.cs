namespace WoWCrucible.Core;

public static class M2AttachmentCatalog
{
    private static readonly string[] Names =
    [
        "Left wrist", "Right palm", "Left palm", "Right elbow", "Left elbow", "Right shoulder", "Left shoulder", "Right knee",
        "Left knee", "Right hip", "Left hip", "Helmet", "Back", "Right shoulder (horizontal)", "Left shoulder (horizontal)", "Bust",
        "Bust 2", "Face", "Above character", "Ground", "Top of head", "Left palm 2", "Right palm 2", "Pre-cast left",
        "Pre-cast right", "Pre-cast center", "Right back sheath", "Left back sheath", "Middle back sheath", "Belly", "Left back", "Right back",
        "Left hip sheath", "Right hip sheath", "Bust 3 / spell impact", "Palm 3", "Right palm alternate", "Demolisher vehicle", "Demolisher vehicle 2", "Vehicle seat 1",
        "Vehicle seat 2", "Vehicle seat 3", "Vehicle seat 4", "Vehicle seat 5", "Vehicle seat 6", "Vehicle seat 7", "Vehicle seat 8", "Left foot",
        "Right foot", "Shield without glove", "Lower spine", "Alternate right shoulder", "Alternate left shoulder", "Belt buckle", "Crossbow sheath", "Head top"
    ];

    public static string Name(uint id) => id < Names.Length ? Names[id] : $"Attachment {id:N0}";
}
