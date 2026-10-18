using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WoWCrucible.Core;
using WoWCrucible.Desktop;

internal static class GuidedEditorsTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var session = new DesktopWorkspaceSession(new DesktopSettings());
        TestItems(session);
        TestQuests(session);
        var root = Path.Combine(Path.GetTempPath(), "Crucible-Guided-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { TestOpening(root); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS guided editors: dependent item choices, custom value preservation, rarity colors, complete quest field coverage, unknown flags/races, reference kinds, format detection and native file routing.");
    }

    private static void TestItems(DesktopWorkspaceSession session)
    {
        Require(ItemSemanticCatalog.QualityColor(6) == "#e6cc80" && ItemSemanticCatalog.QualityColor(7) == "#e6cc80", "Wrath artifact/heirloom colors.");
        using var view = new ItemCreatorView(session);
        var itemClass = Field<ComboBox>(view, "_class"); var subtype = Field<ComboBox>(view, "_subclass"); var inventory = Field<ComboBox>(view, "_inventory");
        foreach (var choice in ((IEnumerable)itemClass.ItemsSource!).Cast<object>().ToArray())
        {
            itemClass.SelectedItem = choice;
            var classId = Id(choice);
            Require(Options(subtype).SequenceEqual(ItemSemanticCatalog.Subclasses(classId)), "Incomplete subclass choices: " + classId);
            foreach (var sub in ((IEnumerable)subtype.ItemsSource!).Cast<object>().ToArray())
            {
                subtype.SelectedItem = sub;
                Require(Options(inventory).SequenceEqual(ItemSemanticCatalog.InventoryTypes(classId, Id(sub))), "Stale inventory options after changing subtype.");
                if (classId == 4) Require(!Options(inventory).Intersect(new[] { 13, 15, 17, 21, 22, 25, 26 }).Any(), "Armor offered weapon slots.");
            }
        }
        Select(itemClass, 4); Select(subtype, 4);
        Require(!Field<ComboBox>(view, "_sheath").IsVisible && !Field<NumericUpDown>(view, "_soundOverride").IsVisible, "Plate armor exposed weapon-only controls.");
        Select(subtype, 6);
        Require(Field<ComboBox>(view, "_sheath").IsVisible, "Shield lost its valid sheath field.");
        Select(itemClass, 2); Select(subtype, 10);
        Require(Options(inventory).SequenceEqual(new[] { 17 }), "Staff must use two-hand equipment slot.");
        Require(Field<NumericUpDown>(view, "_damageMin").IsVisible, "Weapon damage is hidden for weapons.");
        Select(itemClass, 0);
        Require(!Field<NumericUpDown>(view, "_damageMin").IsVisible && !Field<TabItem>(view, "_statsTab").IsVisible, "Inapplicable combat controls shown on a new consumable.");
        var row = new Dictionary<string, object?> { ["entry"] = 9999, ["name"] = "Custom fixture", ["class"] = 4, ["subclass"] = 77, ["InventoryType"] = 99, ["Quality"] = 7, ["dmg_min1"] = 23, ["dmg_max1"] = 25, ["SoundOverrideSubclass"] = -1 };
        view.LoadRow(row);
        var draft = (ItemDraft)Call(view, "Draft")!;
        Require(draft.Subclass == 77 && draft.InventoryType == 99 && draft.DamageMin == 23, "Opening custom item silently reset data.");
        Require(Field<NumericUpDown>(view, "_damageMin").IsVisible, "Nonstandard existing value became invisible.");
        Select(itemClass, 2);
        Require(!Options(subtype).Contains(77) && !Options(inventory).Contains(99), "Changing item class retained invalid choice options.");
    }

    private static void TestQuests(DesktopWorkspaceSession session)
    {
        var table = QuestTemplateAdapter.CreatePortableTable();
        Require(table.Columns.All(column => QuestFieldCatalog.Get(column.Name) is { Description.Length: > 0, Label.Length: > 0 }), "A portable quest field has no verified explanation.");
        Require(QuestFieldCatalog.Get("ItemDropQuantity1")!.Reference is null && QuestFieldCatalog.Get("RewardAmount1")!.Reference is null, "A quantity was offered an item ID picker.");
        Require(QuestFieldCatalog.Get("RewardFactionID1")!.Reference == ReferenceDomain.Faction, "Faction search is missing.");
        using var view = new QuestWorkspaceView(session);
        var values = QuestTemplateAdapter.CreateDefaultValues(table).ToDictionary(pair => pair.Key, pair => pair.Value);
        values["Flags"] = 0x80000000u; values["QuestType"] = 73; values["AllowableRaces"] = 0x100000u;
        view.OpenQuestRow(values);
        Require(Field<ComboBox>(view, "_questType").SelectedItem!.ToString()!.Contains("73"), "Unknown quest type mislabeled as normal.");
        var flags = Field<Dictionary<uint, CheckBox>>(view, "_flagBoxes"); flags[8].IsChecked = true;
        var result = (IReadOnlyDictionary<string, object?>)Call(view, "Values")!;
        Require(Convert.ToUInt32(result["Flags"]) == 0x80000008u && Convert.ToInt32(result["QuestType"]) == 73, "Semantic control lost unknown data.");
        // Traverse logical containers: this test does not show or focus a desktop window.
        var controls = Walk(Field<TabControl>(view, "_fieldTabs")).ToArray();
        var human = controls.OfType<CheckBox>().First(box => Equals(box.Content, "Human")); human.IsChecked = true;
        result = (IReadOnlyDictionary<string, object?>)Call(view, "Values")!;
        Require(Convert.ToUInt32(result["AllowableRaces"]) == 0x100001u, "Race selection erased a custom race bit.");
        Require(controls.OfType<TextBlock>().Any(label => label.Text == "Money reward or cost (copper)"), "Human-readable money units missing.");
        Require(controls.OfType<Button>().Count(button => Equals(button.Content, "Find by name…")) > 20, "Reference search coverage is incomplete.");
        ReferencePickerRequest? requested = null;
        view.ReferenceLookupRequested += (_, request) => requested = request;
        var objective = controls.OfType<StackPanel>().First(panel => panel.Children.OfType<TextBlock>().Any(label => label.Text == "Creature or object objective 1"));
        Walk(objective).OfType<ComboBox>().Single().SelectedIndex = 1;
        Walk(objective).OfType<Button>().Single(button => Equals(button.Content, "Find by name…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(requested?.Domain == ReferenceDomain.GameObject, "Object objective opened the creature picker.");
        requested!.Apply(1289);
        result = (IReadOnlyDictionary<string, object?>)Call(view, "Values")!;
        Require(Convert.ToInt32(result["RequiredNpcOrGo1"]) == -1289, "Object objective lost the negative encoding.");
        var preserved = result.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString());
        var portable = new QuestPortableDraft(preserved, new([42], [43])); view.OpenDraft(portable);
        result = (IReadOnlyDictionary<string, object?>)Call(view, "Values")!;
        Require(preserved.All(pair => result[pair.Key]?.ToString() == pair.Value), "Portable quest round-trip changed fields.");
        Require(Field<TextBox>(view, "_creatureStarters").Text == "42", "Portable quest links lost.");
    }

    private static void TestOpening(string root)
    {
        Program.WriteSpellFixture(root);
        var disguised = Path.Combine(root, "data.bin"); File.Copy(Path.Combine(root, "Spell.dbc"), disguised);
        Require(FileOpenCatalog.Detect(disguised).Kind == FileEditorKind.Table, "Content signature ignored.");
        var script = Path.Combine(root, "test.sql"); File.WriteAllText(script, "SELECT 42;");
        var csv = Path.Combine(root, "rows.csv"); File.WriteAllText(csv, "ID,Name\n42,Test\n");
        Require(FileOpenCatalog.Detect(csv).Kind == FileEditorKind.StructuredRows, "CSV destination ambiguity ignored.");
        var talent = Path.Combine(root, "talents.json"); File.WriteAllText(talent, "{\"format\":\"twc-workspace\",\"trees\":[]}");
        Require(FileOpenCatalog.Detect(talent).Kind == FileEditorKind.Talents, "Talent workspace not identified.");
        var generic = Path.Combine(root, "plain.json"); File.WriteAllText(generic, "{\"format\":1}");
        Require(FileOpenCatalog.Detect(generic).Kind == FileEditorKind.StructuredRows, "Generic numeric format property was misidentified.");
        var png = Path.Combine(root, "image.png"); BlpTextureService.WritePng(png, new RgbaTexture(2, 2, Enumerable.Repeat((byte)255, 16).ToArray()), false);
        Require(FileOpenCatalog.Detect(png).Kind == FileEditorKind.Image, "PNG not identified.");
        var window = new MainWindow(new DesktopSettings { BackupChoiceRemembered = true });
        try
        {
            window.OpenQuestWorkspace();
            Capture(window, "quest-fields.png");
            window.OpenItemWorkbench();
            var item = Field<ItemCreatorView>(Field<ItemWorkbenchView>(window, "_itemWorkbenchView"), "_creator");
            Select(Field<ComboBox>(item, "_class"), 4);
            Select(Field<ComboBox>(item, "_subclass"), 4);
            Capture(window, "item-armor.png");
            Pump(window.LoadPathAsync(script));
            var sql = Field<SqlWorkspaceView>(window, "_sqlWorkspaceView");
            Require(Field<TextBox>(sql, "_query").Text == "SELECT 42;", "Open did not populate the SQL editor.");
            Pump(window.LoadPathAsync(csv));
            var host = window.FindControl<Border>("FeatureWorkspaceHost")!;
            Require(Walk(host.Child!).OfType<TextBlock>().Any(label => label.Text == "Where should this data open?"), "Ambiguous rows did not ask for destination.");
            Pump(window.LoadPathAsync(png));
            var texture = Field<TextureWorkspaceView>(window, "_textureWorkspaceView");
            Require(Field<RgbaTexture>(texture, "_editTexture").Width == 2, "Open image did not load editable pixels.");
            Pump((Task)Call(texture, "ResetEditAsync")!);
            Require(Field<RgbaTexture>(texture, "_editTexture").Width == 2, "Image reset failed.");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<Control> Walk(Control control)
    {
        yield return control;
        IEnumerable<Control> children = control switch
        {
            Panel panel => panel.Children, Decorator decorator when decorator.Child is { } child => [child],
            ItemsControl items => items.ItemsSource is IEnumerable source ? source.Cast<object>().OfType<Control>() : items.Items.OfType<Control>(),
            ContentControl content when content.Content is Control child => [child], _ => []
        };
        foreach (var child in children) foreach (var nested in Walk(child)) yield return nested;
    }
    private static void Capture(MainWindow window, string name)
    {
        typeof(WorkspaceShellTestSuite).GetMethod("ArrangeWindow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window, 1440, 900]);
        typeof(WorkspaceShellTestSuite).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window.FindControl<Grid>("RootLayout")!, 1440, 900, name]);
    }

    private static int Id(object value) => (int)value.GetType().GetProperty("Value")!.GetValue(value)!;
    private static int[] Options(ComboBox combo) => ((IEnumerable)combo.ItemsSource!).Cast<object>().Select(Id).ToArray();
    private static void Select(ComboBox combo, int value) => combo.SelectedItem = ((IEnumerable)combo.ItemsSource!).Cast<object>().First(option => Id(option) == value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pump(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(task.IsCompleted, "Native operation timed out."); task.GetAwaiter().GetResult();
    }
}
