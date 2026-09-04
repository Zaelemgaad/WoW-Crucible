namespace WoWCrucible.Core;

internal static class CrossBuildFieldAliasCatalog
{
    private const string Mop = "mop-18414";
    private const string Legion = "legion-26972";
    private const string ItemSparse = "ITEMSPARSE";

    private static readonly IReadOnlyDictionary<AliasScope, IReadOnlyDictionary<string, string>> Aliases = Build();

    public static string? DonorField(string hostProfileId, string donorProfileId, string canonicalTable, string hostField)
    {
        var scope = new AliasScope(hostProfileId, donorProfileId, canonicalTable);
        return Aliases.TryGetValue(scope, out var fields) && fields.TryGetValue(hostField, out var donorField) ? donorField : null;
    }

    private static IReadOnlyDictionary<AliasScope, IReadOnlyDictionary<string, string>> Build()
    {
        var itemSparse = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Quality"] = "OverallQualityID",
            ["BuyCount"] = "VendorStackCount",
            ["RequiredSpell"] = "RequiredAbility",
            ["RequiredReputationFaction"] = "MinFactionID",
            ["RequiredReputationRank"] = "MinReputation",
            ["DamageType"] = "Damage_damageType",
            ["Delay"] = "ItemDelay",
            ["RangedModRange"] = "ItemRange",
            ["PageText"] = "PageID",
            ["StartQuest"] = "StartQuestID",
            ["Sheath"] = "SheatheType",
            ["RandomProperty"] = "RandomSelect",
            ["RandomSuffix"] = "ItemRandomSuffixGroupID",
            ["Area"] = "ZoneBound",
            ["Map"] = "InstanceBound",
            ["TotemCategory"] = "TotemCategoryID",
            ["SocketBonus"] = "Socket_match_enchantment_ID",
            ["ArmorDamageModifier"] = "QualityModifier",
            ["Duration"] = "DurationInInventory",
            ["ItemLimitCategory"] = "LimitCategory",
            ["HolidayID"] = "RequiredHoliday"
        };
        for (var index = 0; index < 10; index++)
        {
            itemSparse[$"ItemStatType[{index}]"] = $"StatModifier_bonusStat[{index}]";
            itemSparse[$"ItemStatValue[{index}]"] = $"StatModifier_bonusAmount[{index}]";
            itemSparse[$"ItemScalingValue[{index}]"] = $"StatPercentEditor[{index}]";
            itemSparse[$"ItemSocketCostRate[{index}]"] = $"StatPercentageOfSocket[{index}]";
        }
        for (var index = 0; index < 3; index++) itemSparse[$"Color[{index}]"] = $"SocketType[{index}]";

        var unitBlood = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CombatBloodSpurtFront[0]"] = "PlayerCritBloodSpurtID",
            ["CombatBloodSpurtFront[1]"] = "PlayerHitBloodSpurtID",
            ["CombatBloodSpurtBack[0]"] = "PlayerOmniCritBloodSpurtID",
            ["CombatBloodSpurtBack[1]"] = "PlayerOmniHitBloodSpurtID"
        };

        return new Dictionary<AliasScope, IReadOnlyDictionary<string, string>>
        {
            [new(Mop, Legion, ItemSparse)] = itemSparse,
            [new(Mop, Legion, "UNITBLOOD")] = unitBlood
        };
    }

    private readonly record struct AliasScope(string HostProfileId, string DonorProfileId, string CanonicalTable);
}
