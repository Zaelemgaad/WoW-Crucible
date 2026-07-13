using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ManifestPolicyForm : Form
{
    private readonly TextBox _allowed = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox _forbidden = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox _count = new() { Width = 120 };
    public PatchManifestPolicy? Policy { get; private set; }

    public ManifestPolicyForm(PatchManifestPolicy? policy)
    {
        Text = "Patch Manifest Content Policy"; Width = 760; Height = 570; StartPosition = FormStartPosition.CenterParent;
        _allowed.Lines = (policy?.AllowedGlobs ?? []).ToArray(); _forbidden.Lines = (policy?.ForbiddenGlobs ?? []).ToArray(); _count.Text = policy?.ExpectedEntryCount?.ToString() ?? string.Empty;
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12), ColumnCount = 1, RowCount = 7 };
        table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.Percent, 50)); table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.Percent, 50)); table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Allowed archive globs (one per line; blank allows all). Use * within a folder and ** across folders.", AutoSize = true }, 0, 0); table.Controls.Add(_allowed, 0, 1);
        table.Controls.Add(new Label { Text = "Forbidden archive globs (one per line). Example: DBFilesClient\\** or **\\*.m2", AutoSize = true }, 0, 2); table.Controls.Add(_forbidden, 0, 3);
        var countPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; countPanel.Controls.Add(new Label { Text = "Expected exact entry count (blank disables):", AutoSize = true, Margin = new(0, 7, 8, 0) }); countPanel.Controls.Add(_count); table.Controls.Add(countPanel, 0, 4);
        table.Controls.Add(new Label { Text = "Policies are enforced when saving, validating, and building. Matching is case-insensitive against paths inside the MPQ.", AutoSize = true }, 0, 5);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var save = new Button { Text = "Apply Policy", DialogResult = DialogResult.OK, AutoSize = true }; var clear = new Button { Text = "Clear Policy", AutoSize = true }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        clear.Click += (_, _) => { _allowed.Clear(); _forbidden.Clear(); _count.Clear(); };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(clear); table.Controls.Add(buttons, 0, 6); Controls.Add(table); AcceptButton = save; CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            int? count = null;
            if (!string.IsNullOrWhiteSpace(_count.Text))
            {
                if (!int.TryParse(_count.Text, out var parsed) || parsed < 0) { MessageBox.Show(this, "Expected entry count must be a non-negative whole number.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); e.Cancel = true; return; }
                count = parsed;
            }
            var allowed = Lines(_allowed); var forbidden = Lines(_forbidden);
            Policy = allowed.Length == 0 && forbidden.Length == 0 && count is null ? null : new(allowed, forbidden, count);
        };
    }

    private static string[] Lines(TextBox box) => box.Lines.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
