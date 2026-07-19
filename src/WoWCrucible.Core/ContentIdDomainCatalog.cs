namespace WoWCrucible.Core;

public sealed record ContentIdSourceDefinition(string Kind, string Name, string? Column = null);
public sealed record ContentIdDomainPolicy(
    ContentIdDomain Domain,
    ContentIdDomain RegistryNamespace,
    uint RecommendedStart,
    uint Maximum,
    IReadOnlyList<ContentIdSourceDefinition> Sources,
    string Guidance)
{
    public override string ToString() => RegistryNamespace == Domain ? Domain.ToString() : $"{Domain} (shares {RegistryNamespace})";
}

public static class ContentIdDomainCatalog
{
    private static readonly IReadOnlyDictionary<ContentIdDomain, ContentIdDomainPolicy> Policies = new Dictionary<ContentIdDomain, ContentIdDomainPolicy>
    {
        [ContentIdDomain.Item] = Policy(ContentIdDomain.Item, 100_000, uint.MaxValue, [Sql("item_template", "entry"), Dbc("Item")], "Items share one ID between item_template and Item.dbc."),
        [ContentIdDomain.ItemSet] = Policy(ContentIdDomain.ItemSet, 10_000, uint.MaxValue, [Dbc("ItemSet")], "Item-set IDs are stored in ItemSet.dbc and referenced from item templates."),
        [ContentIdDomain.Spell] = Policy(ContentIdDomain.Spell, 100_000, uint.MaxValue, [Sql("spell_dbc", "ID"), Dbc("Spell")], "Spell.dbc is authoritative; spell_dbc may replace complete server records."),
        [ContentIdDomain.CreatureTemplate] = Policy(ContentIdDomain.CreatureTemplate, 100_000, uint.MaxValue, [Sql("creature_template", "entry")], "Creature template IDs are server SQL identities."),
        [ContentIdDomain.CreatureModelData] = Policy(ContentIdDomain.CreatureModelData, 100_000, uint.MaxValue, [Dbc("CreatureModelData")], "Creature model-data IDs are client DBC identities."),
        [ContentIdDomain.CreatureDisplayInfo] = Policy(ContentIdDomain.CreatureDisplayInfo, 100_000, uint.MaxValue, [Dbc("CreatureDisplayInfo")], "Creature display IDs are client DBC identities referenced by server templates."),
        [ContentIdDomain.CreatureDisplayInfoExtra] = Policy(ContentIdDomain.CreatureDisplayInfoExtra, 100_000, uint.MaxValue, [Dbc("CreatureDisplayInfoExtra")], "Creature display-extra IDs are client appearance identities."),
        [ContentIdDomain.GameObject] = Policy(ContentIdDomain.GameObject, 100_000, uint.MaxValue, [Sql("gameobject_template", "entry")], "Gameobject template IDs are server SQL identities."),
        [ContentIdDomain.Race] = Policy(ContentIdDomain.Race, 12, 31, [Dbc("ChrRaces")], "WotLK race IDs participate in 32-bit masks; only IDs 1 through 31 are allocatable."),
        [ContentIdDomain.Class] = Policy(ContentIdDomain.Class, 12, 31, [Dbc("ChrClasses")], "WotLK class IDs participate in 32-bit masks; only IDs 1 through 31 are allocatable."),
        [ContentIdDomain.Faction] = Policy(ContentIdDomain.Faction, 10_000, uint.MaxValue, [Dbc("Faction")], "Faction IDs are client DBC identities."),
        [ContentIdDomain.Mount] = new(ContentIdDomain.Mount, ContentIdDomain.Spell, 100_000, uint.MaxValue, [Sql("spell_dbc", "ID"), Dbc("Spell")], "Mounts use spell IDs; Mount and Spell reservations deliberately share one registry namespace."),
        [ContentIdDomain.Quest] = Policy(ContentIdDomain.Quest, 100_000, uint.MaxValue, [Sql("quest_template", "ID")], "Quest IDs are server SQL identities."),
        [ContentIdDomain.Custom] = Policy(ContentIdDomain.Custom, 100_000, uint.MaxValue, [], "Custom IDs have no automatic source mapping; supply an explicit occupied-ID list.")
    };

    public static IReadOnlyList<ContentIdDomainPolicy> All => Policies.Values.OrderBy(policy => policy.Domain).ToArray();
    public static ContentIdDomainPolicy Get(ContentIdDomain domain) => Policies.TryGetValue(domain, out var policy) ? policy : throw new ArgumentOutOfRangeException(nameof(domain));
    public static ContentIdDomain RegistryNamespace(ContentIdDomain domain) => Get(domain).RegistryNamespace;

    private static ContentIdDomainPolicy Policy(ContentIdDomain domain, uint start, uint maximum, IReadOnlyList<ContentIdSourceDefinition> sources, string guidance)
        => new(domain, domain, start, maximum, sources, guidance);
    private static ContentIdSourceDefinition Sql(string table, string column) => new("SQL", table, column);
    private static ContentIdSourceDefinition Dbc(string table) => new("DBC", table);
}
