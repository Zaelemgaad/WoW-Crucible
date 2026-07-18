using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class ItemCreatorView : UserControl, IDisposable
{
    private sealed record NamedValue(int Value, string Name) { public override string ToString() => Name; }
    private readonly DesktopWorkspaceSession _session;
    private readonly NumericUpDown _entry = Number(1, uint.MaxValue, 1);
    private readonly TextBox _name = new() { Text = "New Custom Item" };
    private readonly ComboBox _class = Choice((0,"Consumable"),(1,"Container"),(2,"Weapon"),(3,"Gem"),(4,"Armor"),(5,"Reagent"),(6,"Projectile"),(7,"Trade Goods"),(9,"Recipe"),(11,"Quiver"),(12,"Quest"),(13,"Key"),(15,"Miscellaneous"),(16,"Glyph"));
    private readonly ComboBox _subclass = Choice((0,"Generic"));
    private readonly NumericUpDown _display = Number(0, uint.MaxValue);
    private readonly ComboBox _quality = Choice((0,"Poor"),(1,"Common"),(2,"Uncommon"),(3,"Rare"),(4,"Epic"),(5,"Legendary"),(6,"Artifact"),(7,"Heirloom"));
    private readonly ComboBox _inventory = Choice((0,"Non-equippable"),(1,"Head"),(2,"Neck"),(3,"Shoulder"),(4,"Shirt"),(5,"Chest"),(6,"Waist"),(7,"Legs"),(8,"Feet"),(9,"Wrist"),(10,"Hands"),(11,"Finger"),(12,"Trinket"),(13,"One-hand weapon"),(14,"Shield"),(15,"Ranged"),(16,"Back"),(17,"Two-hand weapon"),(18,"Bag"),(19,"Tabard"),(20,"Robe"),(21,"Main-hand weapon"),(22,"Off-hand weapon"),(23,"Held in off-hand"),(24,"Ammo"),(25,"Thrown"),(26,"Ranged right"),(27,"Quiver"),(28,"Relic"));
    private readonly NumericUpDown _itemLevel = Number(1, 1000, 1); private readonly NumericUpDown _requiredLevel = Number(0, 255);
    private readonly NumericUpDown _buy = Number(0, uint.MaxValue); private readonly NumericUpDown _sell = Number(0, uint.MaxValue);
    private readonly ComboBox _bonding = Choice((0,"No binding"),(1,"Bind on pickup"),(2,"Bind on equip"),(3,"Bind on use"),(4,"Quest item"));
    private readonly NumericUpDown _flags = Number(0, uint.MaxValue); private readonly NumericUpDown _armor = Number(0, 100000);
    private readonly NumericUpDown _damageMin = Number(0, 100000); private readonly NumericUpDown _damageMax = Number(0, 100000); private readonly NumericUpDown _delay = Number(0, 10000); private readonly NumericUpDown _durability = Number(0, 100000);
    private readonly NumericUpDown _itemSet = Number(0, uint.MaxValue);
    private readonly ComboBox[] _statTypes = Enumerable.Range(0,10).Select(_ => StatChoice()).ToArray(); private readonly NumericUpDown[] _statValues = Enumerable.Range(0,10).Select(_ => Number(-100000,100000)).ToArray();
    private readonly NumericUpDown[] _spellIds = Enumerable.Range(0,5).Select(_ => Number(0,int.MaxValue)).ToArray(); private readonly ComboBox[] _spellTriggers = Enumerable.Range(0,5).Select(_ => SpellTriggerChoice()).ToArray();
    private readonly NumericUpDown[] _spellCharges = Enumerable.Range(0,5).Select(_ => Number(short.MinValue,short.MaxValue)).ToArray(); private readonly NumericUpDown[] _spellPpm = Enumerable.Range(0,5).Select(_ => Number(0,1000)).ToArray();
    private readonly NumericUpDown[] _spellCooldowns = Enumerable.Range(0,5).Select(_ => Number(-1,int.MaxValue,-1)).ToArray(); private readonly NumericUpDown[] _spellCategories = Enumerable.Range(0,5).Select(_ => Number(0,ushort.MaxValue)).ToArray(); private readonly NumericUpDown[] _spellCategoryCooldowns = Enumerable.Range(0,5).Select(_ => Number(-1,int.MaxValue,-1)).ToArray();
    private readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _tooltip = new() { Spacing = 3, Margin = new Thickness(16) };
    private readonly TextBox _sql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly M2PreviewView _model = new(); private readonly TextBlock _modelStatus = Status("Load an extracted WotLK M2 with its companion SKIN to preview geometry.");
    private readonly TextBox _displayDbcPath = new(); private readonly TextBox _displaySchemaPath = new(); private readonly TextBox _assetLibraryPath = new();
    private readonly ComboBox _resolvedModels = new() { PlaceholderText = "Resolved model source" };
    private readonly TextBox _characterModelPath = new(); private readonly TextBox _characterSkinPath = new();
    private readonly ComboBox _wearSources = new() { PlaceholderText = "Resolved armor texture source" };
    private readonly ComboBox _appearanceSkins = new() { PlaceholderText = "Skin" }; private readonly ComboBox _appearanceFaces = new() { PlaceholderText = "Face" };
    private readonly ComboBox _appearanceFacialHair = new() { PlaceholderText = "Facial hair / features" }; private readonly ComboBox _appearanceHair = new() { PlaceholderText = "Hair" };
    private readonly ComboBox _appearanceSources = new() { PlaceholderText = "Character appearance provenance" }; private readonly TextBlock _appearanceStatus = Status("Load decoded character choices to use CharSections instead of a bare base atlas.");
    private AssetComparisonIndex? _appearanceIndex; private CharacterAppearancePreviewPlan? _appearancePlan; private bool _suppressAppearance;
    private readonly TextBlock _displayDetails = Status("Resolve the current display ID to see every ItemDisplayInfo model, texture, icon, geoset, and visual field.");
    private ItemDisplayInfoRecord? _resolvedDisplay;
    private readonly TextBlock _status = Status("Offline portable schema ready."); private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private readonly Button _commit = AccentButton("Insert into connected database");
    private ItemWritePlan? _pendingInsert;
    private uint? _loadedEntry;
    private SpellTooltipCatalog? _spellCatalog; private CancellationTokenSource? _spellDecode;

    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public ItemCreatorView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        _displayDbcPath.Text = string.IsNullOrWhiteSpace(session.Settings.CoreDbcPath) ? string.Empty : Path.Combine(session.Settings.CoreDbcPath, "ItemDisplayInfo.dbc");
        _displaySchemaPath.Text = session.Settings.SchemaDefinitionPath;
        _assetLibraryPath.Text = !string.IsNullOrWhiteSpace(session.Settings.ProcessedAssetLibraryPath) ? session.Settings.ProcessedAssetLibraryPath : Directory.Exists(@"G:\Crucible-Extras-Processed") ? @"G:\Crucible-Extras-Processed" : string.Empty;
        _characterModelPath.Text = session.Settings.ItemPreviewCharacterModelPath; _characterSkinPath.Text = session.Settings.ItemPreviewCharacterSkinPath;
        _class.SelectionChanged += (_, _) => { UpdateSubclassChoices(); RefreshPreview(); };
        foreach (var number in Numbers()) number.ValueChanged += (_, _) => RefreshPreview();
        foreach (var spellId in _spellIds) spellId.ValueChanged += (_, _) => ScheduleSpellDecode();
        foreach (var combo in Combos().Where(combo => !ReferenceEquals(combo, _class))) combo.SelectionChanged += (_, _) => RefreshPreview();
        foreach (var text in new[] { _name, _description }) text.TextChanged += (_, _) => RefreshPreview();

        var basics = Form(("Entry ID",_entry),("Name",_name),("Item class",_class),("Subclass",_subclass),("Display ID",_display),("Quality",_quality),("Inventory slot",_inventory),("Item level",_itemLevel),("Required level",_requiredLevel),("Binding",_bonding),("Item set ID",_itemSet),("Description",_description));
        var combat = Form(("Buy price (copper)",_buy),("Sell price (copper)",_sell),("Armor",_armor),("Minimum damage",_damageMin),("Maximum damage",_damageMax),("Weapon delay (ms)",_delay),("Durability",_durability),("Raw flags",_flags));
        var editTabs = new TabControl { Items = { new TabItem { Header="Basics", Content=new ScrollViewer { Content=basics } }, new TabItem { Header="Combat & value", Content=new ScrollViewer { Content=combat } }, new TabItem { Header="10 stat slots", Content=StatsPage() }, new TabItem { Header="5 spell effects", Content=SpellsPage() } } };

        var resolveDisplay = AccentButton("Resolve current display ID"); resolveDisplay.Click += async (_, _) => await ResolveDisplayAsync(true);
        var loadResolved = new Button { Content = "Load selected resolved model" }; loadResolved.Click += async (_, _) => await LoadResolvedModelAsync();
        var loadModel = new Button { Content = "Load other extracted M2…" }; loadModel.Click += async (_, _) => await LoadModelAsync();
        var clearModel = new Button { Content = "Clear" }; clearModel.Click += (_, _) => { _model.ClearGeometry(); _modelStatus.Text = "Model preview cleared."; };
        var browseCharacter = new Button { Content = "Character M2…" }; browseCharacter.Click += async (_, _) => await PickFileAsync(_characterModelPath, "Choose a playable character M2", "*.m2");
        var browseCharacterSkin = new Button { Content = "Base skin…" }; browseCharacterSkin.Click += async (_, _) => await PickTextureAsync(_characterSkinPath, "Choose that character's base skin atlas");
        var fillCharacter = new Button { Content = "Use extracted Human female" }; fillCharacter.Click += async (_, _) => { FillDefaultCharacter(); await LoadAppearanceChoicesAsync(); };
        var loadAppearance = new Button { Content = "Load decoded character choices" }; loadAppearance.Click += async (_, _) => await LoadAppearanceChoicesAsync();
        var previewEquipped = AccentButton("Preview equipped on character"); previewEquipped.Click += async (_, _) => await PreviewEquippedAsync();
        var browseDisplay = new Button { Content = "DBC…" }; browseDisplay.Click += async (_, _) => await PickFileAsync(_displayDbcPath, "Choose ItemDisplayInfo.dbc", "*.dbc");
        var browseSchema = new Button { Content = "Schema…" }; browseSchema.Click += async (_, _) => await PickFileAsync(_displaySchemaPath, "Choose WotLK schema XML", "*.xml");
        var browseLibrary = new Button { Content = "Assets…" }; browseLibrary.Click += async (_, _) => await PickFolderAsync(_assetLibraryPath, "Choose processed asset library");
        var resolverPaths = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 7, RowSpacing = 5, Children = { _displayDbcPath, WithColumn(browseDisplay,1), WithRow(_displaySchemaPath,1), WithRow(WithColumn(browseSchema,1),1), WithRow(_assetLibraryPath,2), WithRow(WithColumn(browseLibrary,1),2) } };
        var characterPaths = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 7, RowSpacing = 5, Children = { _characterModelPath, WithColumn(browseCharacter,1), WithRow(_characterSkinPath,1), WithRow(WithColumn(browseCharacterSkin,1),1) } };
        var appearanceChoices = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 7, RowSpacing = 5, Children = { _appearanceSkins, WithColumn(_appearanceFaces,1), WithRow(_appearanceFacialHair,1), WithRow(WithColumn(_appearanceHair,1),1), WithRow(_appearanceSources,2) } }; Grid.SetColumnSpan(_appearanceSources,2);
        foreach(var combo in new[]{_appearanceSkins,_appearanceFaces,_appearanceFacialHair,_appearanceHair,_appearanceSources})combo.SelectionChanged+=async(_,_)=>{if(!_suppressAppearance)await LoadAppearanceChoicesAsync();};
        var modelHeader = new StackPanel { Spacing = 7, Children = { new TextBlock { Text="Live item display resolution", FontWeight=FontWeight.SemiBold }, resolverPaths, new WrapPanel { Children = { resolveDisplay, loadResolved, loadModel, clearModel } }, _resolvedModels, _displayDetails, new Separator(), new TextBlock { Text="Equipped armor preview", FontWeight=FontWeight.SemiBold }, new TextBlock { Text="Choose a playable character M2. Load decoded choices to build its real CharSections skin, face, underwear, facial features, hair texture, and hair geosets; the base-atlas field remains a manual fallback.",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#9AA5B7") }, characterPaths, appearanceChoices, _appearanceStatus, _wearSources, new WrapPanel { Children = { fillCharacter, loadAppearance, previewEquipped } } } };
        var modelPage = new Grid { RowDefinitions = new("2*,Auto,3*,Auto"), Children = { new ScrollViewer { Content=modelHeader }, WithRow(new GridSplitter { ResizeDirection=GridResizeDirection.Rows, Background=Brush.Parse("#2B3445") },1), WithRow(new Border { Background=Brush.Parse("#090D14"), Child=_model },2), WithRow(_modelStatus,3) } };
        var previewTabs = new TabControl { Items = { new TabItem { Header="Tooltip", Content=new ScrollViewer { Content=new Border { Background=Brush.Parse("#080911"), BorderBrush=Brush.Parse("#7C6639"), BorderThickness=new Thickness(1), Margin=new Thickness(8), Child=_tooltip } } }, new TabItem { Header="3D model", Content=modelPage }, new TabItem { Header="SQL preview", Content=_sql } } };

        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background=Brush.Parse("#2B3445") };
        var workspace = new Grid { ColumnDefinitions = new("3*,Auto,2*"), Children = { editTabs, WithColumn(splitter,1), WithColumn(previewTabs,2) } };
        var preview = new Button { Content="Refresh change plan" }; preview.Click += (_, _) => RefreshSql();
        var export = new Button { Content="Export SQL…" }; export.Click += async (_, _) => await ExportAsync();
        _commit.Click += (_, _) => PrepareInsert();
        var actions = new WrapPanel { Children = { preview, export, _commit, _status } };
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), Children = { workspace, WithRow(actions,1), WithRow(_confirmation,2) } };
        Content = root; UpdateSubclassChoices(); RefreshPreview(); RefreshSchemaStatus();
    }

    public void LoadRow(IReadOnlyDictionary<string, object?> row)
    {
        _entry.Value=Decimal(row,"entry",1);_name.Text=Text(row,"name");Set(_class,Int(row,"class"));UpdateSubclassChoices();Set(_subclass,Int(row,"subclass"));
        _display.Value=Decimal(row,"displayid");Set(_quality,Int(row,"Quality"));Set(_inventory,Int(row,"InventoryType"));_itemLevel.Value=Decimal(row,"ItemLevel",1);_requiredLevel.Value=Decimal(row,"RequiredLevel");
        _buy.Value=Decimal(row,"BuyPrice");_sell.Value=Decimal(row,"SellPrice");Set(_bonding,Int(row,"bonding"));_flags.Value=Decimal(row,"Flags");_armor.Value=Decimal(row,"armor");
        _damageMin.Value=Decimal(row,"dmg_min1");_damageMax.Value=Decimal(row,"dmg_max1");_delay.Value=Decimal(row,"delay");_durability.Value=Decimal(row,"MaxDurability");_itemSet.Value=Decimal(row,"itemset");_description.Text=Text(row,"description");
        for(var index=0;index<10;index++){Set(_statTypes[index],Int(row,$"stat_type{index+1}"));_statValues[index].Value=Decimal(row,$"stat_value{index+1}");}
        for(var index=0;index<5;index++){var slot=index+1;_spellIds[index].Value=Decimal(row,$"spellid_{slot}");Set(_spellTriggers[index],Int(row,$"spelltrigger_{slot}"));_spellCharges[index].Value=Decimal(row,$"spellcharges_{slot}");_spellPpm[index].Value=Decimal(row,$"spellppmRate_{slot}");_spellCooldowns[index].Value=Decimal(row,$"spellcooldown_{slot}",-1);_spellCategories[index].Value=Decimal(row,$"spellcategory_{slot}");_spellCategoryCooldowns[index].Value=Decimal(row,$"spellcategorycooldown_{slot}",-1);}
        _loadedEntry=(uint)(_entry.Value??0);_commit.Content="Apply decoded fields to existing item";RefreshPreview();RefreshSql();_status.Text=$"Loaded live item {_loadedEntry} with decoded names. Unmapped/custom fields remain editable in SQL Studio."; _ = ResolveDisplayAsync(false); ScheduleSpellDecode();
    }

    private Control StatsPage()
    {
        var grid = new Grid { ColumnDefinitions=new("Auto,2*,*"), RowDefinitions=new(string.Join(',',Enumerable.Repeat("Auto",11))), RowSpacing=5, ColumnSpacing=8, Margin=new Thickness(12) };
        AddText(grid,"Slot",0,0,true); AddText(grid,"Stat",0,1,true); AddText(grid,"Value",0,2,true);
        for(var index=0;index<10;index++){ AddText(grid,$"Stat {index+1}",index+1,0); AddControl(grid,_statTypes[index],index+1,1); AddControl(grid,_statValues[index],index+1,2); }
        return new ScrollViewer { Content=grid };
    }

    private Control SpellsPage()
    {
        var stack=new StackPanel { Spacing=8,Margin=new Thickness(10) };
        stack.Children.Add(new TextBlock { Text="Spell IDs must exist in the effective client/server spell data. Trigger controls tooltip wording; cooldown -1 uses the spell default.",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#9AA5B7") });
        var decode=AccentButton("Decode spell names and tooltip text");decode.Click+=async(_,_)=>await DecodeSpellTooltipsAsync(true,CancellationToken.None);stack.Children.Add(decode);
        for(var index=0;index<5;index++)
        {
            var slot=index; var find=new Button{Content="Find…"}; find.Click+=(_,_)=>ReferenceLookupRequested?.Invoke(this,new(ReferenceDomain.Spell,$"Item spell effect {slot+1}",(uint)(_spellIds[slot].Value??0),selected=>_spellIds[slot].Value=selected));
            stack.Children.Add(new Expander { Header=$"Spell effect {index+1}", IsExpanded=index==0, Content=Form(("Spell ID",Row(_spellIds[index],find)),("Trigger",_spellTriggers[index]),("Charges",_spellCharges[index]),("Proc per minute",_spellPpm[index]),("Cooldown (ms)",_spellCooldowns[index]),("Category ID",_spellCategories[index]),("Category cooldown",_spellCategoryCooldowns[index])) });
        }
        return new ScrollViewer { Content=stack };
    }

    private ItemDraft Draft() => new((uint)(_entry.Value??1),_name.Text??string.Empty,Selected(_class),Selected(_subclass),(uint)(_display.Value??0),Selected(_quality),Selected(_inventory),(uint)(_itemLevel.Value??1),(uint)(_requiredLevel.Value??0),(uint)(_buy.Value??0),(uint)(_sell.Value??0),(uint)Selected(_bonding),(uint)(_flags.Value??0),(float)(_armor.Value??0),(float)(_damageMin.Value??0),(float)(_damageMax.Value??0),(uint)(_delay.Value??0),(uint)(_durability.Value??0),_description.Text??string.Empty,_statTypes.Select((type,index)=>new ItemStatDraft(Selected(type),(int)(_statValues[index].Value??0))).ToArray(),_spellIds.Select((id,index)=>new ItemSpellDraft((int)(id.Value??0),Selected(_spellTriggers[index]),(int)(_spellCharges[index].Value??0),(float)(_spellPpm[index].Value??0),(int)(_spellCooldowns[index].Value??-1),(int)(_spellCategories[index].Value??0),(int)(_spellCategoryCooldowns[index].Value??-1))).ToArray(),(uint)(_itemSet.Value??0));
    private DatabaseTableCapability Table() => _session.DatabaseCapabilities?.FindTable("item_template") ?? ItemTemplateAdapter.CreatePortableTable();
    private ItemWritePlan Plan() => ItemTemplateAdapter.CreatePlan(Draft(),Table());

    private void RefreshPreview()
    {
        _confirmation.IsVisible=false; _pendingInsert=null; var item=Draft(); _tooltip.Children.Clear();
        AddTooltip(string.IsNullOrWhiteSpace(item.Name)?"Unnamed Item":item.Name,QualityBrush(item.Quality),16,FontWeight.Bold);
        AddTooltip($"Item Level {item.ItemLevel}",Brush.Parse("#D2BE78")); if(item.Bonding!=0) AddTooltip(BondingName(item.Bonding),Brushes.White);
        if(item.InventoryType!=0) AddPair(SelectedName(_inventory),SelectedName(_subclass)); if(item.Armor>0) AddTooltip($"{item.Armor:0.##} Armor",Brushes.White);
        if(item.DamageMin>0||item.DamageMax>0){ var speed=item.Delay/1000f; AddPair($"{item.DamageMin:0.##} - {item.DamageMax:0.##} Damage",speed>0?$"Speed {speed:0.00}":""); if(speed>0) AddTooltip($"({(item.DamageMin+item.DamageMax)/2f/speed:0.0} damage per second)",Brushes.White); }
        foreach(var stat in (item.Stats??[]).Take(10).Where(stat=>stat.Value!=0)){ var primary=stat.Type is 0 or 1 or 3 or 4 or 5 or 6 or 7; AddTooltip(primary?$"{(stat.Value>0?"+":"")}{stat.Value} {StatName(stat.Type)}":SecondaryStatText(stat.Type,stat.Value),primary?Brushes.White:Brush.Parse("#1EFF00")); }
        if(item.RequiredLevel>0) AddTooltip($"Requires Level {item.RequiredLevel}",Brushes.White); if(item.MaxDurability>0) AddTooltip($"Durability {item.MaxDurability} / {item.MaxDurability}",Brushes.White);
        foreach(var spell in (item.Spells??[]).Take(5).Where(spell=>spell.SpellId!=0)){ var prefix=spell.Trigger switch{0 or 5=>"Use",1=>"Equip",2=>"Chance on hit",4=>"Use",6=>"Use",_=>"Effect"};SpellTooltipRecord? decoded=null;if(_spellCatalog is not null)_spellCatalog.Records.TryGetValue((uint)spell.SpellId,out decoded);var label=decoded is null?$"Spell #{spell.SpellId}":decoded.Name+(string.IsNullOrWhiteSpace(decoded.Subtext)?string.Empty:$" ({decoded.Subtext})");var text=spell.Trigger==6?$"Use: Teaches {label}.":$"{prefix}: {label}.";var detail=decoded?.PreferredItemText(spell.Trigger);if(!string.IsNullOrWhiteSpace(detail))text+=$" {detail}";if(spell.Charges!=0)text+=$" ({Math.Abs(spell.Charges)} charge{(Math.Abs(spell.Charges)==1?"":"s")})";AddTooltip(text,Brush.Parse("#1EFF00")); }
        if(item.ItemSetId>0) AddTooltip($"Item Set: {item.ItemSetId}",Brush.Parse("#FFD100")); if(!string.IsNullOrWhiteSpace(item.Description)) AddTooltip($"\"{item.Description.Trim()}\"",Brush.Parse("#FFD100"));
        if(item.SellPrice>0) AddTooltip($"Sell Price: {Price(item.SellPrice)}",Brushes.White); AddTooltip($"Entry {item.Entry}  •  Display {item.DisplayId}",Brush.Parse("#787882"),11);
    }

    private void RefreshSql(){ try{ var plan=Plan(); _sql.Text=plan.PreviewSql()+(plan.OmittedFields.Count==0?"":$"\n\n-- Target schema omitted: {string.Join(", ",plan.OmittedFields)}"); _status.Text=$"Plan targets {plan.Table} with {plan.Values.Count:N0} columns; {plan.OmittedFields.Count:N0} omitted."; }catch(Exception exception){_status.Text=exception.Message;} }
    private async Task ExportAsync(){ try{RefreshSql();var file=await Storage().SaveFilePickerAsync(new FilePickerSaveOptions{Title="Export item SQL",SuggestedFileName=$"item-{(uint)(_entry.Value??0)}.sql",FileTypeChoices=[new FilePickerFileType("SQL"){Patterns=["*.sql"]}]});var path=file?.TryGetLocalPath();if(path is not null)await File.WriteAllTextAsync(path,(_sql.Text??string.Empty)+Environment.NewLine);}catch(Exception exception){_status.Text=$"Export failed: {exception.Message}";} }
    private void PrepareInsert(){ try{ if(!_session.DatabaseTested||_session.DatabaseProfile is null){_status.Text="Connect and verify Server & SQL before writing.";return;} _pendingInsert=Plan();if(_loadedEntry is{ } loaded&&(uint)(_entry.Value??0)!=loaded)throw new InvalidOperationException("The decoded editor will not silently change a primary item ID. Use Full item copy for a variant or SQL Studio for an explicit key migration.");var editing=_loadedEntry is not null;var cancel=new Button{Content="Cancel"};var confirm=AccentButton(editing?$"Confirm update item {_loadedEntry}":$"Confirm insert item {(uint)(_entry.Value??0)}");cancel.Click+=(_,_)=>{_confirmation.IsVisible=false;_pendingInsert=null;};confirm.Click+=async(_,_)=>await CommitAsync(confirm);_confirmation.Child=new Grid{ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=8,Children={new TextBlock{Text=editing?$"Update Crucible's decoded fields for '{_name.Text}' in {_session.DatabaseProfile.Database}.item_template? Custom/unmapped columns remain unchanged.":$"Insert '{_name.Text}' into {_session.DatabaseProfile.Database}.item_template? Existing IDs are refused, never replaced.",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center},WithColumn(cancel,1),WithColumn(confirm,2)}};_confirmation.IsVisible=true;}catch(Exception exception){_status.Text=exception.Message;} }
    private async Task CommitAsync(Button button){ if(_pendingInsert is null||_session.DatabaseProfile is null)return;try{button.IsEnabled=false;if(_loadedEntry is{ } loaded)await new SqlWorkspaceService().UpdateRowAsync(_session.DatabaseProfile,Table(),new Dictionary<string,object?>{{"entry",loaded}},_pendingInsert.Values.ToDictionary(pair=>pair.Key,pair=>(object?)pair.Value,StringComparer.OrdinalIgnoreCase));else await new ItemTemplateService().InsertAsync(_session.DatabaseProfile,_pendingInsert);_status.Text=_loadedEntry is null?"Item inserted transactionally. Restart or reload the world server as required by the detected core.":"Decoded item fields updated transactionally; custom/unmapped columns were preserved.";_confirmation.IsVisible=false;_pendingInsert=null;}catch(Exception exception){_status.Text=$"Item write failed: {exception.Message}";DesktopCrashLogger.Log("Item write failed",exception);}finally{button.IsEnabled=true;} }
    private async Task LoadModelAsync(){try{var files=await Storage().OpenFilePickerAsync(new FilePickerOpenOptions{Title="Choose an extracted WotLK M2",AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("WotLK M2"){Patterns=["*.m2"]}]});var path=files.FirstOrDefault()?.TryGetLocalPath();if(path is null)return;_modelStatus.Text="Loading model…";var geometry=await Task.Run(()=>M2PreviewGeometryService.Load(path));_model.SetGeometry(geometry);_modelStatus.Text=$"{Path.GetFileName(path)} · {geometry.Submeshes.Count(section=>section.Visible):N0}/{geometry.Submeshes.Count:N0} base geosets · {geometry.TriangleIndices.Count/3:N0} triangles";}catch(Exception exception){_modelStatus.Text=$"Model load failed: {exception.Message}";} }

    private async Task ResolveDisplayAsync(bool loadFirstModel)
    {
        try
        {
            var displayId=(uint)(_display.Value??0); if(displayId==0){_resolvedDisplay=null;_resolvedModels.ItemsSource=Array.Empty<string>();_wearSources.ItemsSource=Array.Empty<ItemWearSourceSet>();_displayDetails.Text="Display ID 0 has no ItemDisplayInfo record.";return;}
            var dbc=_displayDbcPath.Text?.Trim()??string.Empty; if(!File.Exists(dbc))throw new FileNotFoundException("Configure the server's ItemDisplayInfo.dbc path.",dbc);
            _modelStatus.Text=$"Resolving display {displayId:N0}…";
            var resolved=await Task.Run(()=>ItemDisplayInfoService.Resolve(dbc,EmptyNull(_displaySchemaPath.Text),displayId,Selected(_class),Selected(_subclass),Selected(_inventory),EmptyNull(_assetLibraryPath.Text)));
            _resolvedDisplay=resolved; var models=resolved.ExistingModels.ToArray(); _resolvedModels.ItemsSource=models; _resolvedModels.SelectedIndex=models.Length>0?0:-1;
            var wearSources=ItemEquipmentPreviewService.FindWearSources(resolved); _wearSources.ItemsSource=wearSources; _wearSources.SelectedIndex=wearSources.Count>0?0:-1;
            var assets=resolved.Assets.Select(asset=>$"{asset.Kind} {asset.Slot}: {asset.Name} · {(asset.ExistingPaths.Count==0?"not found in processed library":$"{asset.ExistingPaths.Count:N0} source file(s)")}");
            _displayDetails.Text=$"Display {resolved.Id:N0} · geosets {string.Join(", ",resolved.GeosetGroups)} · helmet visibility {string.Join(", ",resolved.HelmetGeosetVisibility)} · flags 0x{resolved.Flags:X8}\nSpell visual {resolved.SpellVisualId:N0} · item visual {resolved.ItemVisualId:N0} · particle color {resolved.ParticleColorId:N0} · sound group {resolved.GroupSoundIndex:N0}\n{string.Join("\n",assets)}";
            _modelStatus.Text=models.Length>0?$"Resolved display {displayId:N0} to {resolved.Assets.Count:N0} dependency slot(s) and {models.Length:N0} extracted model source(s).":wearSources.Count>0?$"Armor display resolved to {wearSources.Count:N0} gender/provenance choice(s). Use Equipped armor preview; wearable armor normally has no standalone M2.":"DBC record resolved, but no extracted standalone M2 or wearable texture matched the configured processed library. Every expected client path is listed above.";
            _session.Settings.ProcessedAssetLibraryPath=_assetLibraryPath.Text??string.Empty; _session.Settings.Save();
            if(loadFirstModel&&models.Length>0)await LoadResolvedModelAsync();
        }
        catch(Exception exception){_resolvedDisplay=null;_resolvedModels.ItemsSource=Array.Empty<string>();_wearSources.ItemsSource=Array.Empty<ItemWearSourceSet>();_displayDetails.Text=$"Display resolution failed: {exception.Message}";_modelStatus.Text=_displayDetails.Text;if(loadFirstModel)DesktopCrashLogger.Log("Item display resolution failed",exception);else DesktopCrashLogger.Debug("ITEM","automatic-display-resolution-unavailable",("display_id",(uint)(_display.Value??0)),("error",exception.Message));}
    }

    private async Task LoadResolvedModelAsync()
    {
        if(_resolvedModels.SelectedItem is not string path||!File.Exists(path)){_modelStatus.Text="Resolve a display and select an extracted model source first.";return;}
        try
        {
            _modelStatus.Text=$"Loading {Path.GetFileName(path)}…"; var geometry=await Task.Run(()=>M2PreviewGeometryService.Load(path, visibilityMode:M2PreviewVisibilityMode.AllGeosets)); _model.SetGeometry(geometry);
            var provenance=Path.GetDirectoryName(path); var texture=_resolvedDisplay?.Assets.Where(asset=>asset.Kind=="model-texture").SelectMany(asset=>asset.ExistingPaths).Where(candidate=>Path.GetExtension(candidate).Equals(".png",StringComparison.OrdinalIgnoreCase)).OrderByDescending(candidate=>Path.GetDirectoryName(candidate)?.Equals(provenance,StringComparison.OrdinalIgnoreCase)==true).FirstOrDefault();
            _model.SetTexture(texture); _modelStatus.Text=$"{Path.GetFileName(path)} · {geometry.Submeshes.Count(section=>section.Visible):N0}/{geometry.Submeshes.Count:N0} geosets · {geometry.TriangleIndices.Count/3:N0} triangles{(texture is null?" · no resolved PNG texture":$" · {Path.GetFileName(texture)}")}";
        }
        catch(Exception exception){_modelStatus.Text=$"Resolved model could not be rendered: {exception.Message}";DesktopCrashLogger.Log("Resolved item model preview failed",exception);}
    }

    private async Task PreviewEquippedAsync()
    {
        try
        {
            if(_resolvedDisplay is null) await ResolveDisplayAsync(false);
            if(_resolvedDisplay is null) throw new InvalidOperationException("Resolve a valid ItemDisplayInfo display ID first.");
            if(_wearSources.SelectedItem is not ItemWearSourceSet source) throw new InvalidOperationException("No extracted wear-texture source was found for this display. Choose an asset library containing its Item\\TextureComponents files.");
            var modelPath=_characterModelPath.Text?.Trim()??string.Empty; var skinPath=_characterSkinPath.Text?.Trim()??string.Empty;
            if(!File.Exists(modelPath)) throw new FileNotFoundException("Choose an extracted playable character M2.",modelPath);
            var normalized=modelPath.Replace('/','\\'); if(normalized.Contains("\\Female\\",StringComparison.OrdinalIgnoreCase)&&source.Source.EndsWith(" · male",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The selected wear source is male but the character model path is female. Choose a female source."); if(normalized.Contains("\\Male\\",StringComparison.OrdinalIgnoreCase)&&source.Source.EndsWith(" · female",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The selected wear source is female but the character model path is male. Choose a male source.");
            _modelStatus.Text=$"Composing display {_resolvedDisplay.Id:N0} from {source.Source}…";
            ComposedCharacterAppearance? appearance=null; ItemEquipmentPreview preview; var groupVariants=new Dictionary<int,int>();
            if(_appearanceIndex is not null&&_appearancePlan?.SelectedSource is not null)
            {
                appearance=await Task.Run(()=>CharacterAppearancePreviewService.Compose(_appearanceIndex,_appearancePlan)); preview=await Task.Run(()=>ItemEquipmentPreviewService.Compose(appearance.Body,_resolvedDisplay,Selected(_inventory),source));
                foreach(var pair in _appearancePlan.Geosets.GroupVariants)groupVariants[pair.Key]=pair.Value;
            }
            else preview=await Task.Run(()=>ItemEquipmentPreviewService.Compose(skinPath,_resolvedDisplay,Selected(_inventory),source));
            foreach(var pair in preview.Geosets.GroupVariants)groupVariants[pair.Key]=pair.Value;
            var selection=new M2GeosetSelection(groupVariants,appearance is null?preview.Geosets.Source:$"Decoded CharSections + {preview.Geosets.Source}");
            var geometry=await Task.Run(()=>M2PreviewGeometryService.Load(modelPath,visibilityMode:M2PreviewVisibilityMode.BaseAppearance,geosetSelection:selection));
            _model.SetGeometry(geometry);
            if(appearance?.Hair is not null){var textures=new Dictionary<int,RgbaTexture>();foreach(var slot in geometry.TextureSlots){if(slot.Type==1)textures[slot.Index]=preview.Atlas;else if(slot.Type==6)textures[slot.Index]=appearance.Hair;}if(textures.Count>0)_model.SetDecodedTextures(textures);else _model.SetDecodedTexture(preview.Atlas);}else _model.SetDecodedTexture(preview.Atlas);
            _session.Settings.ItemPreviewCharacterModelPath=modelPath; _session.Settings.ItemPreviewCharacterSkinPath=skinPath; _session.Settings.Save();
            var geosets=preview.Geosets.GroupVariants.Count==0?"no equipment geoset override":string.Join(", ",preview.Geosets.GroupVariants.Select(pair=>$"{pair.Key}:{pair.Value}"));
            _modelStatus.Text=$"Equipped display {_resolvedDisplay.Id:N0} · {source.Source} · wear slots {(preview.AppliedSlots.Count==0?"none":string.Join(", ",preview.AppliedSlots))} · {geosets}{(preview.MissingSlots.Count==0?string.Empty:$" · missing source slots {string.Join(", ",preview.MissingSlots)}")}";
        }
        catch(Exception exception){_modelStatus.Text=$"Equipped preview failed: {exception.Message}";DesktopCrashLogger.Log("Equipped item preview failed",exception);}
    }

    private async Task LoadAppearanceChoicesAsync()
    {
        try
        {
            var modelPath=_characterModelPath.Text?.Trim()??string.Empty;if(!File.Exists(modelPath))throw new FileNotFoundException("Choose a playable character M2 first.",modelPath);
            var identity=CharacterAppearanceService.Infer(Path.GetDirectoryName(modelPath)??string.Empty,Path.GetFileName(modelPath))??throw new InvalidDataException("The selected model path does not identify a supported playable race and sex.");
            var dbcFolder=Path.GetDirectoryName(_displayDbcPath.Text?.Trim()??string.Empty)??string.Empty;if(!File.Exists(Path.Combine(dbcFolder,"CharSections.dbc")))throw new FileNotFoundException("Point the display DBC field at the server DBC folder containing CharSections.dbc.");
            _appearanceStatus.Text="Indexing processed assets and resolving CharSections choices…";
            var libraryRoot=Path.GetFullPath(_assetLibraryPath.Text?.Trim()??string.Empty);if(_appearanceIndex is null||!_appearanceIndex.LibraryRoot.Equals(libraryRoot,StringComparison.OrdinalIgnoreCase))_appearanceIndex=await Task.Run(()=>AssetComparisonService.BuildIndex(libraryRoot));
            var skin=(_appearanceSkins.SelectedItem as CharacterBaseSkin)?.Id;var face=(_appearanceFaces.SelectedItem as CharacterSection)?.Id;var facial=(_appearanceFacialHair.SelectedItem as CharacterSection)?.Id;var hair=(_appearanceHair.SelectedItem as CharacterSection)?.Id;var source=(_appearanceSources.SelectedItem as CharacterAppearanceSource)?.FullPath;
            var preferred=Directory.GetParent(modelPath)?.Name;var plan=await Task.Run(()=>CharacterAppearancePreviewService.Build(_appearanceIndex,dbcFolder,identity,skin,face,facial,hair,source,preferred));_appearancePlan=plan;
            _suppressAppearance=true;try{_appearanceSkins.ItemsSource=plan.Skins;_appearanceSkins.SelectedItem=plan.SelectedSkin;_appearanceFaces.ItemsSource=plan.Faces;_appearanceFaces.SelectedItem=plan.SelectedFace;_appearanceFacialHair.ItemsSource=plan.FacialHair;_appearanceFacialHair.SelectedItem=plan.SelectedFacialHair;_appearanceHair.ItemsSource=plan.Hair;_appearanceHair.SelectedItem=plan.SelectedHair;_appearanceSources.ItemsSource=plan.Sources;_appearanceSources.SelectedItem=plan.SelectedSource;}finally{_suppressAppearance=false;}
            _appearanceStatus.Text=plan.Message+(plan.SelectedSource is null?" Select a provenance before previewing.":" Ready for equipped preview.");
        }
        catch(Exception exception){_appearancePlan=null;_appearanceStatus.Text=$"Decoded appearance unavailable: {exception.Message}";}
    }

    private void FillDefaultCharacter()
    {
        try
        {
            var root=Path.Combine(_assetLibraryPath.Text?.Trim()??string.Empty,"Archives","Content","Character","Human","Female"); if(!Directory.Exists(root))throw new DirectoryNotFoundException("The processed library has no Archives\\Content\\Character\\Human\\Female path.");
            var sources=Directory.EnumerateDirectories(root).OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).ToArray();
            var model=sources.Select(path=>Directory.EnumerateFiles(path,"*",SearchOption.TopDirectoryOnly).FirstOrDefault(file=>Path.GetFileName(file).Equals("HumanFemale.m2",StringComparison.OrdinalIgnoreCase))).FirstOrDefault(path=>path is not null);
            var skin=sources.Select(path=>Directory.EnumerateFiles(path,"*",SearchOption.TopDirectoryOnly).FirstOrDefault(file=>Path.GetFileName(file).Equals("HumanFemaleSkin00_00.blp",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(file).Equals("HumanFemaleSkin00_00.png",StringComparison.OrdinalIgnoreCase))).FirstOrDefault(path=>path is not null);
            if(model is null||skin is null)throw new FileNotFoundException("The Human female path does not contain both HumanFemale.m2 and HumanFemaleSkin00_00 BLP/PNG sources."); _characterModelPath.Text=model;_characterSkinPath.Text=skin;
            if(_wearSources.ItemsSource is IEnumerable<ItemWearSourceSet> wear){var female=wear.FirstOrDefault(value=>value.Source.EndsWith(" · female",StringComparison.OrdinalIgnoreCase));if(female is not null)_wearSources.SelectedItem=female;}
            _modelStatus.Text=$"Selected extracted Human female model from {Path.GetFileName(Path.GetDirectoryName(model))} and base skin from {Path.GetFileName(Path.GetDirectoryName(skin))}. Review the explicit wear source below, then preview.";
        }
        catch(Exception exception){_modelStatus.Text=$"Default character selection failed: {exception.Message}";}
    }

    private void ScheduleSpellDecode()
    {
        _spellDecode?.Cancel();_spellDecode?.Dispose();_spellDecode=new CancellationTokenSource();_ = DecodeSpellTooltipsAsync(false,_spellDecode.Token);
    }

    private async Task DecodeSpellTooltipsAsync(bool announce,CancellationToken token)
    {
        try
        {
            if(!announce)await Task.Delay(350,token);var dbcFolder=Path.GetDirectoryName(_displayDbcPath.Text?.Trim()??string.Empty)??string.Empty;var path=Path.Combine(dbcFolder,"Spell.dbc");if(!File.Exists(path)){if(announce)_status.Text="Spell.dbc is not beside ItemDisplayInfo.dbc; configure the server DBC folder first.";return;}
            var catalog=_spellCatalog is not null&&_spellCatalog.Matches(path)?_spellCatalog:await Task.Run(()=>SpellTooltipService.Load(path),token);token.ThrowIfCancellationRequested();_spellCatalog=catalog;RefreshPreview();
            if(announce){var ids=_spellIds.Select(value=>(uint)(value.Value??0)).Where(value=>value!=0).Distinct().ToArray();var found=ids.Count(id=>catalog.Records.ContainsKey(id));_status.Text=$"Decoded {found:N0}/{ids.Length:N0} configured item spell ID(s) from {catalog.Records.Count:N0} WotLK spells. Raw client $-tokens remain visible when their value depends on runtime context.";}
        }
        catch(OperationCanceledException){}catch(Exception exception){if(announce)_status.Text=$"Spell tooltip decode failed: {exception.Message}";else DesktopCrashLogger.Debug("ITEM","spell-tooltip-decode-unavailable",("error",exception.Message));}
    }

    private void UpdateSubclassChoices(){ _subclass.ItemsSource=Selected(_class) switch{0=>Values((0,"Consumable"),(1,"Potion"),(2,"Elixir"),(3,"Flask"),(4,"Scroll"),(5,"Food & Drink"),(6,"Item Enhancement"),(7,"Bandage")),1=>Values((0,"Bag"),(1,"Soul Bag"),(2,"Herb Bag"),(3,"Enchanting Bag"),(4,"Engineering Bag"),(5,"Gem Bag")),2=>Values((0,"One-Handed Axe"),(1,"Two-Handed Axe"),(2,"Bow"),(3,"Gun"),(4,"One-Handed Mace"),(5,"Two-Handed Mace"),(6,"Polearm"),(7,"One-Handed Sword"),(8,"Two-Handed Sword"),(10,"Staff"),(13,"Fist Weapon"),(15,"Dagger"),(16,"Thrown"),(18,"Crossbow"),(19,"Wand"),(20,"Fishing Pole")),4=>Values((0,"Miscellaneous Armor"),(1,"Cloth"),(2,"Leather"),(3,"Mail"),(4,"Plate"),(6,"Shield"),(7,"Libram"),(8,"Idol"),(9,"Totem"),(10,"Sigil")),_=>Values((0,"Generic"))};_subclass.SelectedIndex=0; }
    private void SessionChanged(object? sender,EventArgs e)=>RefreshSchemaStatus(); private void RefreshSchemaStatus(){var cap=_session.DatabaseCapabilities;_status.Text=cap?.FindTable("item_template") is{ } table?$"Live schema ready · {cap.Database}.item_template · {table.Columns.Count:N0} columns":"Offline portable schema ready · connect Server & SQL for live deployment.";}
    public void Dispose(){_session.Changed-=SessionChanged;_spellDecode?.Cancel();_spellDecode?.Dispose();_model.Dispose();}
    private IStorageProvider Storage()=>TopLevel.GetTopLevel(this)?.StorageProvider??throw new InvalidOperationException("Item Creator is not attached to the main window.");
    private async Task PickFileAsync(TextBox target,string title,string pattern){var files=await Storage().OpenFilePickerAsync(new FilePickerOpenOptions{Title=title,AllowMultiple=false,FileTypeFilter=[new FilePickerFileType(title){Patterns=[pattern]}]});var path=files.FirstOrDefault()?.TryGetLocalPath();if(path is not null)target.Text=path;}
    private async Task PickTextureAsync(TextBox target,string title){var files=await Storage().OpenFilePickerAsync(new FilePickerOpenOptions{Title=title,AllowMultiple=false,FileTypeFilter=[new FilePickerFileType("Character texture"){Patterns=["*.blp","*.png","*.jpg","*.jpeg","*.bmp","*.tga"]}]});var path=files.FirstOrDefault()?.TryGetLocalPath();if(path is not null)target.Text=path;}
    private async Task PickFolderAsync(TextBox target,string title){var folders=await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions{Title=title,AllowMultiple=false});var path=folders.FirstOrDefault()?.TryGetLocalPath();if(path is not null)target.Text=path;}
    private IEnumerable<NumericUpDown> Numbers()=>new[]{_entry,_display,_itemLevel,_requiredLevel,_buy,_sell,_flags,_armor,_damageMin,_damageMax,_delay,_durability,_itemSet}.Concat(_statValues).Concat(_spellIds).Concat(_spellCharges).Concat(_spellPpm).Concat(_spellCooldowns).Concat(_spellCategories).Concat(_spellCategoryCooldowns);
    private IEnumerable<ComboBox> Combos()=>new[]{_class,_subclass,_quality,_inventory,_bonding}.Concat(_statTypes).Concat(_spellTriggers);
    private void AddTooltip(string text,IBrush brush,double fontSize=13,FontWeight? weight=null)=>_tooltip.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Foreground=brush,FontSize=fontSize,FontWeight=weight??FontWeight.Normal});
    private void AddPair(string left,string right)=>_tooltip.Children.Add(new Grid{ColumnDefinitions=new("*,Auto"),Children={new TextBlock{Text=left,Foreground=Brushes.White},WithColumn(new TextBlock{Text=right,Foreground=Brushes.White},1)}});
    private static Grid Form(params(string Label,Control Input)[] rows){var grid=new Grid{ColumnDefinitions=new("Auto,*"),RowDefinitions=new(string.Join(',',Enumerable.Repeat("Auto",rows.Length))),RowSpacing=7,ColumnSpacing=10,Margin=new Thickness(12)};for(var index=0;index<rows.Length;index++){AddText(grid,rows[index].Label,index,0);AddControl(grid,rows[index].Input,index,1);}return grid;}
    private static Grid Row(Control field,Control button)=>new(){ColumnDefinitions=new("*,Auto"),ColumnSpacing=7,Children={field,WithColumn(button,1)}};
    private static void AddText(Grid grid,string text,int row,int column,bool bold=false){var value=new TextBlock{Text=text,VerticalAlignment=VerticalAlignment.Center,FontWeight=bold?FontWeight.Bold:FontWeight.Normal};Grid.SetRow(value,row);Grid.SetColumn(value,column);grid.Children.Add(value);}private static void AddControl(Grid grid,Control control,int row,int column){Grid.SetRow(control,row);Grid.SetColumn(control,column);grid.Children.Add(control);}
    private static NumericUpDown Number(decimal min,decimal max,decimal value=0)=>new(){Minimum=min,Maximum=max,Value=value,Increment=1};private static ComboBox Choice(params(int Value,string Name)[] values)=>new(){ItemsSource=Values(values),SelectedIndex=0};private static ComboBox StatChoice()=>Choice((0,"None"),(1,"Health"),(3,"Agility"),(4,"Strength"),(5,"Intellect"),(6,"Spirit"),(7,"Stamina"),(12,"Defense rating"),(13,"Dodge rating"),(14,"Parry rating"),(15,"Block rating"),(31,"Hit rating"),(32,"Critical strike rating"),(35,"Resilience rating"),(36,"Haste rating"),(37,"Expertise rating"),(38,"Attack power"),(39,"Ranged attack power"),(43,"Mana regeneration"),(44,"Armor penetration rating"),(45,"Spell power"),(46,"Health regeneration"),(47,"Spell penetration"),(48,"Block value"));private static ComboBox SpellTriggerChoice()=>Choice((0,"Use"),(1,"Equip"),(2,"Chance on hit"),(4,"Soulstone"),(5,"Use (no delay)"),(6,"Learn spell"));
    private static NamedValue[] Values(params(int Value,string Name)[] values)=>values.Select(value=>new NamedValue(value.Value,value.Name)).ToArray();private static int Selected(ComboBox combo)=>combo.SelectedItem is NamedValue value?value.Value:0;private static string SelectedName(ComboBox combo)=>combo.SelectedItem is NamedValue value?value.Name:string.Empty;
    private static void Set(ComboBox combo,int value){if(combo.ItemsSource is IEnumerable<NamedValue> values)combo.SelectedItem=values.FirstOrDefault(item=>item.Value==value)??values.FirstOrDefault();}
    private static object? Value(IReadOnlyDictionary<string,object?> row,string name)=>row.FirstOrDefault(pair=>pair.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;private static decimal Decimal(IReadOnlyDictionary<string,object?> row,string name,decimal fallback=0){try{return Convert.ToDecimal(Value(row,name)??fallback,System.Globalization.CultureInfo.InvariantCulture);}catch{return fallback;}}private static int Int(IReadOnlyDictionary<string,object?> row,string name){try{return Convert.ToInt32(Value(row,name)??0,System.Globalization.CultureInfo.InvariantCulture);}catch{return 0;}}private static string Text(IReadOnlyDictionary<string,object?> row,string name)=>Convert.ToString(Value(row,name),System.Globalization.CultureInfo.InvariantCulture)??string.Empty;
    private static IBrush QualityBrush(int quality)=>Brush.Parse(quality switch{0=>"#9D9D9D",1=>"#FFFFFF",2=>"#1EFF00",3=>"#0070DD",4=>"#A335EE",5=>"#FF8000",6=>"#E6281E",7=>"#00CCFF",_=>"#FFFFFF"});private static string BondingName(uint bonding)=>bonding switch{1=>"Binds when picked up",2=>"Binds when equipped",3=>"Binds when used",4 or 5=>"Quest Item",_=>""};
    private static string StatName(int type)=>type switch{0=>"Mana",1=>"Health",3=>"Agility",4=>"Strength",5=>"Intellect",6=>"Spirit",7=>"Stamina",12=>"Defense Rating",13=>"Dodge Rating",14=>"Parry Rating",15=>"Block Rating",31=>"Hit Rating",32=>"Critical Strike Rating",35=>"Resilience Rating",36=>"Haste Rating",37=>"Expertise Rating",38=>"Attack Power",39=>"Ranged Attack Power",43=>"Mana per 5 sec",44=>"Armor Penetration Rating",45=>"Spell Power",46=>"Health Regeneration",47=>"Spell Penetration",48=>"Block Value",_=>$"Stat {type}"};private static string SecondaryStatText(int type,int value)=>type switch{38=>$"Equip: Increases attack power by {value}.",39=>$"Equip: Increases ranged attack power by {value}.",43=>$"Equip: Restores {value} mana per 5 sec.",45=>$"Equip: Increases spell power by {value}.",46=>$"Equip: Restores {value} health per 5 sec.",47=>$"Equip: Increases spell penetration by {value}.",48=>$"Equip: Increases the block value of your shield by {value}.",_=>$"Equip: Improves your {StatName(type).ToLowerInvariant()} by {value}."};
    private static string Price(uint copper){var gold=copper/10000;var silver=copper%10000/100;var rest=copper%100;return $"{(gold>0?$"{gold}g ":"")}{(silver>0?$"{silver}s ":"")}{rest}c";}private static TextBlock Status(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#99A5B8")};private static Button AccentButton(string text){var button=new Button{Content=text};button.Classes.Add("accent");return button;}private static T WithColumn<T>(T control,int column)where T:Control{Grid.SetColumn(control,column);return control;}private static T WithRow<T>(T control,int row)where T:Control{Grid.SetRow(control,row);return control;}
    private static string? EmptyNull(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
}
