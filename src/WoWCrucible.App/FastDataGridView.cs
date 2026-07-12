namespace WoWCrucible.App;

internal sealed class FastDataGridView : DataGridView
{
    public FastDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
