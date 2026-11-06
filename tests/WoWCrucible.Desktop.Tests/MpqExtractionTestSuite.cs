using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WoWCrucible.Core;
using WoWCrucible.Desktop;
using static Program;

internal static class MpqExtractionTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Crucible-MpqSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.txt");
            var second = Path.Combine(root, "second.txt");
            var localized = Path.Combine(root, "localized.txt");
            File.WriteAllText(first, "first payload");
            File.WriteAllText(second, "second payload");
            File.WriteAllText(localized, "French payload");
            var archive = Path.Combine(root, "selection.mpq");
            var service = new PatchArchiveService();
            service.Create(archive, [new(first, @"Folder\first.txt"), new(second, @"Folder\second.txt"),
                new(localized, @"Folder\first.txt", 0x040C)]);
            var before = SHA256.HashData(File.ReadAllBytes(archive));
            var entries = service.ListFiles(archive).Where(entry => !entry.IsMetadata).ToArray();
            var page = MpqArchiveBrowser.Browse(entries, "Folder");
            var node = page.Nodes.Single(node => node.Entry!.Locale == 0x040C);
            var selected = MpqArchiveBrowser.Select(entries, [node]);
            Require(selected.Count == 1, "One file selection expanded to sibling files or other locales.");
            var destination = Path.Combine(root, "output", "renamed.txt");
            service.ExtractFile(archive, destination, selected[0]);
            Require(File.ReadAllText(destination) == "French payload", "Single-file extraction lost the selected locale.");
            Require(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*", SearchOption.AllDirectories).Length == 1,
                "Single-file extraction wrote unrelated archive members.");
            try { service.ExtractFile(archive, archive, selected[0]); throw new Exception("Source replacement allowed."); }
            catch (InvalidOperationException) { }
            try
            {
                service.ExtractFile(archive, destination, new("missing.txt", 1, 1, 0, 0));
                throw new Exception("Missing member was reported as extracted.");
            }
            catch (Win32Exception) { }
            Require(File.ReadAllText(destination) == "French payload", "Failed extraction damaged the existing destination.");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { service.ExtractFile(archive, destination, selected[0], cancellation.Token); throw new Exception("Cancel ignored."); }
            catch (OperationCanceledException) { }
            var multipleDestination = Path.Combine(root, "multiple");
            service.Extract(archive, multipleDestination, entries.Where(entry => entry.Locale == 0));
            Require(Directory.GetFiles(multipleDestination, "*", SearchOption.AllDirectories).Length == 2 &&
                File.ReadAllText(Path.Combine(multipleDestination, "Folder", "second.txt")) == "second payload",
                "Folder extraction no longer preserves the selected subset and directory structure.");
            Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(archive))), "Extraction changed the source MPQ.");

            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            using var session = new DesktopWorkspaceSession(new DesktopSettings());
            using var view = new MpqWorkspaceView(session);
            var window = new Window { Content = view };
            try
            {
                window.Width = 1200; window.Height = 800; window.ApplyTemplate();
                Field<TabControl>(view, "_tabs").SelectedIndex = 2;
                window.Measure(new Size(1200, 800)); window.Arrange(new Rect(0, 0, 1200, 800));
                Dispatcher.UIThread.RunJobs();
                var chosenFile = window.StorageProvider.TryGetFileFromPathAsync(new Uri(destination)).GetAwaiter().GetResult();
                Require(chosenFile is not null, "Could not create a native destination file for the extraction test.");
                var picker = CoverageStorageProvider.Create();
                var recorded = (CoverageStorageProvider)picker;
                typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, picker);
                Set(view, "_allArchiveEntries", entries);
                Set(view, "_openedArchivePath", archive);
                Field<TextBox>(view, "_archivePath").Text = archive;
                typeof(MpqWorkspaceView).GetMethod("NavigateTree", Private)!.Invoke(view, ["Folder"]);
                var tree = Field<ListBox>(view, "_treeItems");
                var button = Field<Button>(view, "_extractTreeSelection");
                Require(!button.IsEnabled, "Selection extraction is enabled without a selection.");
                tree.SelectedItem = ((IEnumerable<MpqBrowserNode>)tree.ItemsSource!).Single(n => n.Entry!.Locale == 0x040C);
                Require(button.IsEnabled && button.Content!.ToString() == "Extract selected file…", "File action did not track selection.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(recorded.LastSaveOptions?.SuggestedFileName == "first.txt" && recorded.LastFolderOptions is null,
                    $"Selecting one file did not offer saving that file directly: calls={recorded.Calls}, " +
                    $"save={recorded.LastSaveOptions?.SuggestedFileName}, folder={recorded.LastFolderOptions?.Title}.");
                var flat = Field<ListBox>(view, "_browserItems");
                flat.ItemsSource = entries;
                flat.SelectedItems!.Add(entries[0]);
                flat.SelectedItems.Add(entries[1]);
                var multi = Field<Button>(view, "_extractFlatSelection");
                Require(multi.IsEnabled && multi.Content!.ToString()!.Contains("2 files"), "Multi-selection count is wrong.");
                multi.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(recorded.LastFolderOptions?.Title?.Contains("2 selected files") == true,
                    "Multiple selected entries did not use folder extraction.");
                // Invoke the actual row context-menu action while another file remains selected.
                var clicked = ((IEnumerable<MpqBrowserNode>)tree.ItemsSource!).Single(n => n.Name == "second.txt");
                var row = tree.ItemTemplate!.Build(clicked)!;
                var contextExtract = (MenuItem)row.ContextMenu!.Items[0]!;
                recorded.SaveResult = chosenFile;
                Field<TextBox>(view, "_archivePath").Text = "unopened-different-archive.mpq";
                contextExtract.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(() => Field<TextBlock>(view, "_browserStatus").Text?.StartsWith("Extracted ") == true);
                Require(File.ReadAllText(destination) == "second payload" &&
                    Directory.GetFiles(Path.GetDirectoryName(destination)!, "*", SearchOption.AllDirectories).Length == 1,
                    "File context-menu extraction used a previous selection or extracted the whole archive.");
                Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(archive))), "Context extraction changed the MPQ.");
                recorded.SaveResult = null;
                var flatRow = flat.ItemTemplate!.Build(selected[0])!;
                ((MenuItem)flatRow.ContextMenu!.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(recorded.LastSaveOptions?.SuggestedFileName == "first.txt",
                    "Flat-search context menu used the list's multiple selection instead of the clicked file.");
                Console.WriteLine("PASS MPQ extraction: clicked-row context menus, exact bytes/locale, immutable archive, single/multiple selections, cancellation and atomic failure.");
            }
            finally { window.Close(); }
            TestSharedBrowser(archive);
            TestArchiveEditing(root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);

    private static void TestSharedBrowser(string archive)
    {
        var window = new MainWindow(new DesktopSettings());
        try
        {
            window.OpenMpqWorkspace();
            var shared = Field<MpqWorkspaceView>(window, "_mpqWorkspaceView");
            var opened = window.LoadPathAsync(archive); // The File -> Open routing path.
            PumpUntil(() => opened.IsCompleted);
            opened.GetAwaiter().GetResult();
            Require(ReferenceEquals(shared, Field<MpqWorkspaceView>(window, "_mpqWorkspaceView")),
                "File Open created another archive browser.");
            Require(Field<string>(shared, "_openedArchivePath") == archive,
                "File Open did not load the requested archive into the shared browser.");
            window.OpenClientWorkspace();
            var inspector = Field<ClientWorkspaceView>(window, "_clientWorkspaceView");
            var request = Field<EventHandler<string>>(inspector, "OpenArchiveRequested");
            request.Invoke(inspector, archive);
            PumpUntil(() => Field<TextBlock>(shared, "_browserStatus").Text?.StartsWith("Loaded ") == true);
            Require(ReferenceEquals(shared, Field<MpqWorkspaceView>(window, "_mpqWorkspaceView")),
                "Client Inspector created another archive browser.");
            Console.WriteLine("PASS one shared MPQ browser: Archives & Patches, File Open and Client Inspector.");
        }
        finally { window.Close(); }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed()) timeout.Cancel(); };
        timer.Start();
        try { Dispatcher.UIThread.MainLoop(timeout.Token); }
        finally { timer.Stop(); }
        Require(completed(), "Timed out waiting for the UI extraction operation.");
    }

    private static void TestArchiveEditing(string root)
    {
        var fixture = Path.Combine(root, "ArchiveFixture.dbc");
        using (var writer = new BinaryWriter(File.Create(fixture)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WDBC"));
            writer.Write(1); writer.Write(2); writer.Write(8); writer.Write(1);
            writer.Write(900001); writer.Write(7); writer.Write((byte)0);
        }
        var original = File.ReadAllBytes(fixture);
        var archive = Path.Combine(root, "editable.mpq");
        var service = new PatchArchiveService();
        service.Create(archive, [new(fixture, @"DBFilesClient\ArchiveFixture.dbc"),
            new(fixture, @"DBFilesClient\ArchiveFixture.dbc", 0x040C), new(fixture, @"Other\Keep.dbc")]);
        var entries = service.ListFiles(archive).Where(entry => !entry.IsMetadata).ToArray();
        var localized = entries.Single(entry => entry.Locale == 0x040C);
        var column = new DbcColumn(1, 4, 4, "Value", DbcValueType.UInt32);
        var window = new MainWindow(new DesktopSettings { BackupChoiceRemembered = true });
        try
        {
            window.OpenMpqWorkspace();
            var browser = Field<MpqWorkspaceView>(window, "_mpqWorkspaceView");
            Set(browser, "_openedArchivePath", archive);
            var flat = Field<ListBox>(browser, "_browserItems");
            flat.ItemsSource = entries;
            flat.SelectedItem = entries.First(entry => entry.Locale == 0);
            var row = flat.ItemTemplate!.Build(localized)!;
            var open = row.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open in new tab"));
            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var documents = Field<List<DbcDocumentSession>>(window, "_documents");
            PumpUntil(() => documents.Count == 1 && documents[0].ArchiveSource is not null);
            var document = documents.Single();
            Require(document.ArchiveSource!.Entry.Locale == 0x040C, "Open in new tab used the selection instead of the clicked locale.");
            var tabs = Field<WorkspaceTabState>(window, "_workspaceTabs");
            Require(tabs.SelectedId == document.TabId && tabs.Tabs.Any(tab => tab.Title.Contains("editable.mpq")),
                "Archive document did not appear in the main tab bar with its source archive.");
            document.File.SetRaw(0, column, 42);
            var save = (Task<bool>)typeof(MainWindow).GetMethod("SaveCurrentAsync", Private)!.Invoke(window, [false])!;
            PumpUntil(() => save.IsCompleted);
            Require(save.GetAwaiter().GetResult() && !document.File.IsDirty, "Save did not commit the MPQ document.");
            var check = Path.Combine(root, "check.dbc");
            service.ExtractFile(archive, check, localized);
            Require(WdbcFile.Load(check).GetRaw(0, column) == 42, "Saved edit was absent from the original MPQ entry.");
            foreach (var entry in entries.Where(entry => entry.Locale == 0))
            {
                service.ExtractFile(archive, check, entry);
                Require(File.ReadAllBytes(check).SequenceEqual(original), "Saving changed another member or locale.");
            }
            // Saving another member must not invalidate this editor's source check.
            using (var other = new MpqDocumentSource(archive, entries.Single(entry => entry.ArchivePath.StartsWith("Other"))))
            {
                var file = WdbcFile.Load(other.WorkingPath);
                file.SetRaw(0, column, 99); other.Save(file);
            }
            document.File.SetRaw(0, column, 43); document.Save();
            using (var competing = new MpqDocumentSource(archive, localized))
            {
                var file = WdbcFile.Load(competing.WorkingPath);
                file.SetRaw(0, column, 44); competing.Save(file);
            }
            document.File.SetRaw(0, column, 45);
            try { document.Save(); throw new Exception("Conflicting external edit was overwritten."); }
            catch (IOException) { }
            Require(document.File.IsDirty, "Rejected save cleared the editor's unsaved changes.");
            service.ExtractFile(archive, check, localized);
            Require(WdbcFile.Load(check).GetRaw(0, column) == 44, "Conflict rejection changed the MPQ.");
            // Publisher failure must retain unsaved state, too (not just preflight conflicts).
            var failed = WdbcFile.Load(fixture); failed.SetRaw(0, column, 88);
            try { failed.Save(check, false, _ => throw new IOException("simulated publish failure")); }
            catch (IOException) { }
            Require(failed.IsDirty, "Failed container publication marked the table clean.");
            // The + control creates a real tab, and returning selects the same document.
            var strip = window.FindControl<WoWCrucible.Desktop.Controls.WorkspaceTabStrip>("WorkspaceTabs")!;
            var grid = (Grid)strip.Content!;
            grid.Children.OfType<Button>().Single(button => Equals(button.Content, "+"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(tabs.Tabs.Any(tab => tab.Title == "New tab") && tabs.SelectedId != document.TabId, "The + button did not create a tab.");
            typeof(MainWindow).GetMethod("SelectWorkspaceTab", Private)!.Invoke(window, [document.TabId]);
            Require(tabs.SelectedId == document.TabId && document.File.GetRaw(0, column) == 45,
                "Switching tabs discarded unsaved edits.");
            // Avoid a modal save question when disposing this intentionally conflicted test editor.
            document.File.Save(Path.Combine(root, "discard-test.dbc"), false);
            var close = (Task)typeof(MainWindow).GetMethod("CloseWorkspaceTabAsync", Private)!.Invoke(window, [document.TabId])!;
            PumpUntil(() => close.IsCompleted);
            close.GetAwaiter().GetResult();
            Require(documents.Count == 0 && tabs.Tabs.All(tab => tab.Id != document.TabId), "Closing the document left a stale tab.");
            open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => documents.Count == 1 && documents[0].ArchiveSource is not null);
            Require(documents[0].File.GetRaw(0, column) == 44, "Reopening reused a stale working copy.");
            var archiveBeforeSaveAs = SHA256.HashData(File.ReadAllBytes(archive));
            var loosePath = Path.Combine(root, "saved-copy.dbc");
            File.WriteAllBytes(loosePath, []);
            var nativeFile = window.StorageProvider.TryGetFileFromPathAsync(new Uri(loosePath)).GetAwaiter().GetResult();
            var picker = CoverageStorageProvider.Create();
            ((CoverageStorageProvider)picker).SaveResult = nativeFile;
            typeof(TopLevel).GetField("_storageProvider", Private)!.SetValue(window, picker);
            documents[0].File.SetRaw(0, column, 66);
            var saveAs = (Task<bool>)typeof(MainWindow).GetMethod("SaveCurrentAsync", Private)!.Invoke(window, [true])!;
            PumpUntil(() => saveAs.IsCompleted);
            Require(saveAs.GetAwaiter().GetResult() && documents[0].ArchiveSource is null &&
                WdbcFile.Load(loosePath).GetRaw(0, column) == 66 &&
                archiveBeforeSaveAs.SequenceEqual(SHA256.HashData(File.ReadAllBytes(archive))),
                "Save As failed to detach a permanent copy without changing the MPQ.");
            Console.WriteLine("PASS MPQ editing: clicked locale, DBC save/reopen/Save As, preserved members, independent saves, conflict and failure protection, unified tabs and +.");
        }
        finally { window.Close(); }
    }
}
