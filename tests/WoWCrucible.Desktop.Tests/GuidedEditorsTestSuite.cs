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
    public static void Run(bool liveItems = false)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        using var session = new DesktopWorkspaceSession(new DesktopSettings());
        TestItems(session);
        TestQuests(session);
        if (liveItems) TestLiveItems();
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
        var update=Field<TextBox>(view,"_sql").Text!;
        Require(update.StartsWith("UPDATE `item_template` SET") && update.Contains("WHERE `entry` = 9999;") && !update.Contains("SET\n  `entry`"), "Loaded item exported INSERT or changed its primary key.");
        Select(itemClass, 2);
        Require(!Options(subtype).Contains(77) && !Options(inventory).Contains(99), "Changing item class retained invalid choice options.");
        Field<NumericUpDown>(view,"_entry").Value=10000;Call(view,"RefreshSql");
        Require(string.IsNullOrEmpty(Field<TextBox>(view,"_sql").Text),"Invalid primary-key edit left stale exportable SQL.");
        view.NewItem();
        Require(Field<TextBox>(view,"_sql").Text!.StartsWith("INSERT INTO") && Equals(Field<Button>(view,"_commit").Content,"Create item"), "New draft kept update mode.");
        Require(((ItemDraft)Call(view,"Draft")!).DamageMin==0, "New item retained previous item stats.");
        TestItemTooltipFields(session);
    }

    private static void TestItemTooltipFields(DesktopWorkspaceSession session)
    {
        using var view = new ItemCreatorView(session);
        var row = new Dictionary<string, object?>
        {
            ["entry"] = 19019, ["name"] = "Thunderfury, Blessed Blade of the Windseeker", ["class"] = 2, ["subclass"] = 7, ["InventoryType"] = 13, ["Quality"] = 5, ["bonding"] = 1,
            ["Flags"] = 0x80008, ["maxcount"] = 2, ["dmg_min1"] = 185, ["dmg_max1"] = 285, ["delay"] = 1800, ["dmg_min2"] = 406, ["dmg_max2"] = 488, ["dmg_type2"] = 3,
            ["nature_res"] = 150, ["socketColor_1"] = 4, ["socketBonus"] = 3312, ["AllowableClass"] = 32767, ["AllowableRace"] = 512, ["RequiredSkill"] = 356, ["RequiredSkillRank"] = 1,
            ["requiredspell"] = 30449, ["startquest"] = 7785, ["block"] = 0, ["RequiredLevel"] = 60, ["ItemLevel"] = 80
        };
        view.LoadRow(row);
        var draft = (ItemDraft)Call(view, "Draft")!;
        Require(draft is { DamageMin2: 406, DamageMax2: 488, DamageType2: 3, MaxCount: 2, AllowableClass: 32767, AllowableRace: 512, RequiredSkill: 356, RequiredSkillRank: 1, RequiredSpell: 30449, StartQuest: 7785, SocketBonus: 3312 }
            && draft.Resistance(2) == 150 && draft.SocketColor(0) == 4, "Opening an item lost a tooltip field.");
        Require(Field<CheckBox>(view, "_heroic").IsChecked == true && Field<CheckBox>(view, "_uniqueEquipped").IsChecked == true && Field<CheckBox>(view, "_accountBound").IsChecked == false,
            "Tooltip flag checkboxes do not reflect the loaded Flags value.");
        Require(Field<CheckBox[]>(view, "_classChecks").All(check => check.IsChecked == true) && Field<CheckBox[]>(view, "_raceChecks").Count(check => check.IsChecked == true) == 1,
            "Class/race checkboxes do not reflect the loaded masks.");
        var sql = Field<TextBox>(view, "_sql").Text!;
        foreach (var expected in new[] { "`nature_res` = 150", "`dmg_min2` = 406", "`AllowableClass` = 32767", "`AllowableRace` = 512", "`socketColor_1` = 4", "`requiredspell` = 30449", "`maxcount` = 2" })
            Require(sql.Contains(expected), $"Saving the opened item would not keep {expected}:\n{sql}");
        var lines = Field<IReadOnlyList<ItemTooltipLine>>(Field<WoWCrucible.Desktop.Controls.ItemTooltipView>(view, "_tooltip"), "_current").Select(line => line.Left).ToArray();
        foreach (var expected in new[] { "Heroic", "Unique (2)", "+406 - 488 Nature Damage", "(378.9 damage per second)", "+150 Nature Resistance", "Yellow Socket", "Races: Blood Elf", "Requires Level 60", "Item Level 80" })
            Require(lines.Contains(expected), $"Editor tooltip is missing \"{expected}\":\n{string.Join("\n", lines)}");
        Require(!lines.Any(line => line.StartsWith("Entry ") || line.StartsWith("Sell Price")), "Editor tooltip shows lines the game does not.");

        Field<CheckBox>(view, "_heroic").IsChecked = false;
        Field<CheckBox>(view, "_heroic").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(((ItemDraft)Call(view, "Draft")!).Flags == 0x80000, "Unchecking Heroic did not clear only its flag bit.");
        var races = Field<CheckBox[]>(view, "_raceChecks");
        foreach (var check in races) { check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        Require(((ItemDraft)Call(view, "Draft")!).AllowableRace == -1, "Checking every race must store the unrestricted -1 mask.");
        view.NewItem();
        var reset = (ItemDraft)Call(view, "Draft")!;
        Require(reset is { DamageMin2: 0, MaxCount: 0, AllowableClass: -1, AllowableRace: -1, RequiredSpell: 0 } && reset.Resistance(2) == 0, "New item kept the previous item's tooltip fields.");
    }

    /// <summary>Renders real items from the configured realm through the item editor, with the configured client's font and Spell.dbc. Read-only.</summary>
    public static void RenderLiveTooltips(string outputDirectory, IEnumerable<string> itemIds)
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText("src/WoWCrucible.Desktop/bin/Debug/net10.0/Config/desktop.json"))!;
        using var session = new DesktopWorkspaceSession(settings);
        var realms = session.GetRealmsAsync(); Pump(realms);
        if (settings.ActiveRealmId is { } realm) Pump(session.SelectRealmAsync(realm));
        Directory.CreateDirectory(outputDirectory);
        var browser = new ItemBrowserWindow(session);
        try
        {
            Pump((Task)Call(browser, "InitializeAsync")!);
            using var view = new ItemCreatorView(session);
            var tooltip = Field<WoWCrucible.Desktop.Controls.ItemTooltipView>(view, "_tooltip");
            var fontLoad = (Task?)typeof(WoWCrucible.Desktop.Controls.ItemTooltipView).GetField("_assetLoad", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            if (fontLoad is not null) Pump(fontLoad, 600); // A cold client scan can take minutes.
            // A numeric query is an exact item ID; any other text renders every matching item name.
            foreach (var query in itemIds)
            foreach (var row in Search(query))
            {
                var id = Convert.ToString(row.Values["entry"]);
                view.LoadRow(row.Values);
                Pump((Task)view.GetType().GetMethod("DecodeSpellTooltipsAsync", Private)!.Invoke(view, [true, CancellationToken.None])!);
                Dispatcher.UIThread.RunJobs();
                // Render the editor's lines in a window-hosted copy; a control detached from a TopLevel is never templated.
                var probe = new WoWCrucible.Desktop.Controls.ItemTooltipView { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                var host = new Window { Content = probe, Background = Avalonia.Media.Brushes.Black };
                host.ApplyTemplate();
                probe.Display(Field<IReadOnlyList<ItemTooltipLine>>(tooltip, "_current"));
                host.Measure(new Size(560, 1600)); host.Arrange(new Rect(0, 0, 560, 1600));
                var size = probe.Bounds.Size;
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
                bitmap.Render(probe);
                var path = Path.Combine(outputDirectory, $"item-{id}.png"); bitmap.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                Console.WriteLine($"Item {id} raw: " + string.Join(", ", new[] { "ItemLevel", "RequiredLevel", "RequiredSkill", "RequiredSkillRank", "requiredspell", "maxcount", "Flags", "spellid_1", "spellid_2", "spellid_4" }
                    .Select(column => $"{column}={row.Values.FirstOrDefault(pair => pair.Key.Equals(column, StringComparison.OrdinalIgnoreCase)).Value ?? "<absent>"}")));
                Console.WriteLine($"Item {id}: {path}\n  " + string.Join("\n  ", Field<IReadOnlyList<ItemTooltipLine>>(tooltip, "_current").Select(line => line.Right is null ? line.Left : $"{line.Left} | {line.Right}")));
            }
            IEnumerable<SqlRowRecord> Search(string query)
            {
                Field<TextBox>(browser, "_search").Text = query; Pump(browser.SearchAsync());
                var rows = ((IEnumerable)Field<ListBox>(browser, "_results").ItemsSource!).Cast<SqlRowRecord>().ToArray();
                var matches = query.All(char.IsDigit) ? rows.Where(row => Convert.ToString(row.Values["entry"]) == query).ToArray() : rows.Take(12).ToArray();
                if (matches.Length == 0) Console.WriteLine($"Item {query}: not in {session.DatabaseProfile?.Database}");
                return matches;
            }
            var assets = typeof(WoWCrucible.Desktop.Controls.ItemTooltipView).GetField("_assets", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var font = assets.GetType().GetProperty("Font")!.GetValue(assets) as Avalonia.Media.FontFamily;
            var sockets = (System.Collections.ICollection)assets.GetType().GetProperty("Sockets")!.GetValue(assets)!;
            Console.WriteLine($"Font: {font?.Name ?? "fallback (client font not found)"}; socket textures: {sockets.Count}/5");
        }
        finally { browser.Close(); }
    }

    private static void TestLiveItems()
    {
        var settings=JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText("src/WoWCrucible.Desktop/bin/Debug/net10.0/Config/desktop.json"))!;
        using var session=new DesktopWorkspaceSession(settings);
        var discover=session.GetRealmsAsync();Pump(discover);
        Require(discover.Result.Realms.Count==5,"Installed realms missing from the shared session.");
        Pump(session.SelectRealmAsync(1));
        var browser=new ItemBrowserWindow(session);
        try
        {
            Pump((Task)Call(browser,"InitializeAsync")!);
            Require(Field<ComboBox>(browser,"_realm").Items.Count==5,"Item browser did not show the configured realms.");
            var search=Field<TextBox>(browser,"_search");var results=Field<ListBox>(browser,"_results");
            search.Text="17";Pump(browser.SearchAsync());
            var rows=((IEnumerable)results.ItemsSource!).Cast<SqlRowRecord>().ToArray();
            Require(rows.Length==1 && Convert.ToUInt32(rows[0].Values["entry"])==17,"Exact item ID search failed: "+Field<TextBlock>(browser,"_status").Text);
            var row=rows[0];search.Text=Convert.ToString(row.Values["name"]);Pump(browser.SearchAsync());
            Require(((IEnumerable)results.ItemsSource!).Cast<SqlRowRecord>().Any(r=>Convert.ToUInt32(r.Values["entry"])==17),"Item name search lost the known item.");
            using var editor=new ItemCreatorView(session);editor.LoadRow(row.Values);
            Require(Field<TextBox>(editor,"_sql").Text!.StartsWith("UPDATE"),"Live item did not enter edit mode.");
            search.Text="";Pump(browser.SearchAsync());
            Require(((IEnumerable)results.ItemsSource!).Cast<SqlRowRecord>().Count()==50 && Field<Button>(browser,"_next").IsEnabled,"Existing item browse page is incomplete.");
            foreach(var realm in discover.Result.Realms.Where(r=>r.Id!=1))
            {
                Pump(session.SelectRealmAsync(realm.Id));search.Text="17";Pump(browser.SearchAsync());
                Require(((IEnumerable)results.ItemsSource!).Cast<SqlRowRecord>().Count()==1,"Item read failed in "+realm.Name);
                Require(session.DatabaseProfile!.Database==realm.Server.WorldDatabase.Database,"Realm switch retained previous database.");
            }
            Call(editor,"PrepareInsert");
            Require(Field<ItemWritePlan?>(editor,"_pendingInsert") is null && Field<TextBlock>(editor,"_status").Text!.Contains("different database"),"Cross-realm item save was not blocked.");
            Require(DesktopSettings.Load().ActiveRealmId==5,"Last desktop realm did not persist.");
            Console.WriteLine("PASS live item browser: ID/name search, 50-row browse page, all five realm databases, loaded-item UPDATE preview, remembered last realm and cross-realm write prevention. No database writes.");
        }
        finally { browser.Close(); }
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
    private static void Pump(Task task, int seconds = 45)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(task.IsCompleted, "Native operation timed out."); task.GetAwaiter().GetResult();
    }
}
