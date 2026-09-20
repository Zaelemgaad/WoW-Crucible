using System.Numerics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed record M2PreviewMountedModel(M2PreviewGeometry Geometry, Matrix4x4 Transform, RgbaTexture? Texture, string Label, int? ParentAttachmentIndex = null);

public sealed class M2PreviewView : UserControl, IDisposable
{
    private readonly M2PreviewCanvas _canvas = new();
    private readonly WrapPanel _cameraBar = new() { Margin = new Thickness(0, 4) };
    private readonly Button _frame = new() { Content = "\u21BA", Width = 34 };
    private readonly Button _face = new() { Content = "\u25CE", Width = 34 };
    private readonly List<ToggleButton> _cameraButtons = [];
    private readonly Grid _playback = new() { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 5 };
    private readonly Button _play = new() { Content = "\u25B6", Width = 34 };
    private readonly CheckBox _loop = new() { Content = "Loop", IsChecked = true };
    private readonly ComboBox _sequences = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBlock _time = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#EE7777"), IsVisible = false };
    private readonly Slider _timeline = new() { Minimum = 0, Maximum = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000d / 30d) };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private M2PreviewGeometry? _geometry;
    private M2AnimationPose? _pose;
    private double _elapsedBeforePlay;
    private bool _updatingTimeline;
    private bool _updatingChoices;
    private bool _playRequested;

    public M2PreviewView()
    {
        ToolTip.SetTip(_frame, "Frame model"); ToolTip.SetTip(_face, "Focus face"); ToolTip.SetTip(_play, "Play");
        _frame.Click += (_, _) => _canvas.FrameModel(); _face.Click += (_, _) => _canvas.FocusFace();
        _canvas.CameraChanged += (_, _) => UpdateCameraButtons();
        ClipToBounds = true;
        _playback.Children.Add(_play); Grid.SetColumn(_play, 0);
        _playback.Children.Add(_sequences); Grid.SetColumn(_sequences, 1);
        _playback.Children.Add(_loop); Grid.SetColumn(_loop, 2);
        _playback.Children.Add(_timeline); Grid.SetRow(_timeline, 1); Grid.SetColumnSpan(_timeline, 2);
        _playback.Children.Add(_time); Grid.SetColumn(_time, 2); Grid.SetRow(_time, 1);
        _playback.Children.Add(_error); Grid.SetRow(_error, 2); Grid.SetColumnSpan(_error, 3);
        var root = new Grid { RowDefinitions = new("*,Auto,Auto") };
        root.Children.Add(_canvas);
        root.Children.Add(_cameraBar); Grid.SetRow(_cameraBar, 1);
        root.Children.Add(_playback); Grid.SetRow(_playback, 2);
        Content = root;
        _play.Click += (_, _) => TogglePlayback();
        _sequences.SelectionChanged += (_, _) => { if (!_updatingChoices) SelectSequence(); };
        _timeline.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty && !_updatingTimeline) Scrub(_timeline.Value); };
        _timer.Tick += (_, _) => Tick();
        DetachedFromVisualTree += (_, _) => SuspendPlayback();
        AttachedToVisualTree += (_, _) => ResumePlayback();
        _playback.IsVisible = false;
        _cameraBar.IsVisible = true;
    }

    public void SetGeometry(M2PreviewGeometry geometry)
    {
        var sameModel = _geometry?.ModelPath == geometry.ModelPath;
        var oldSequence = sameModel ? SelectedSequence()?.Index : null;
        SuspendPlayback(); var previousTime = sameModel ? _elapsedBeforePlay : 0;
        _geometry = geometry;
        if (!sameModel || _pose?.Vertices.Length != geometry.Vertices.Count || _pose?.BoneTransforms.Length != geometry.Bones.Count) _pose = null;
        _elapsedBeforePlay = previousTime;
        _canvas.SetGeometry(geometry, !sameModel);
        _cameraBar.Children.Clear(); _cameraButtons.Clear();
        _cameraBar.Children.Add(_frame); _cameraBar.Children.Add(_face);
        AddCameraButton(null, "Orbit");
        foreach (var camera in geometry.Cameras) AddCameraButton(camera.Index, camera.Name);
        UpdateCameraButtons();
        _updatingChoices = true;
        _sequences.ItemsSource = geometry.Sequences;
        _playback.IsVisible = geometry.Sequences.Count > 0;
        _sequences.SelectedItem = geometry.Sequences.FirstOrDefault(sequence => sequence.Index == oldSequence)
            ?? geometry.Sequences.FirstOrDefault(sequence => sequence.AnimationId == 0 && sequence.SubAnimationId == 0) ?? geometry.Sequences.FirstOrDefault();
        _updatingChoices = false;
        SelectSequence(previousTime);
    }

    public void ClearGeometry() { SuspendPlayback(); _geometry = null; _pose = null; _playback.IsVisible = false; _cameraBar.Children.Clear(); _cameraButtons.Clear(); _canvas.ClearGeometry(); }
    public void SetTexture(string? previewPath) => _canvas.SetTexture(previewPath);
    public void SetDecodedTexture(RgbaTexture? texture) => _canvas.SetDecodedTexture(texture);
    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures) => _canvas.SetDecodedTextures(textures);
    public void SetSceneTransform(Matrix4x4 transform, string? label = null) => _canvas.SetSceneTransform(transform, label);
    public void SetAttachmentOverlay(bool visible, int? highlightedAttachmentIndex = null) => _canvas.SetAttachmentOverlay(visible, highlightedAttachmentIndex);
    public void SetMountedModels(IEnumerable<M2PreviewMountedModel> models)
    {
        _canvas.SetMountedModels(models); UpdatePlaybackAvailability();
    }
    public void ClearMountedModels() { _canvas.ClearMountedModels(); UpdatePlaybackAvailability(); }
    public M2AnimationPose? SnapshotPose() => _geometry is null || _pose is null ? null : M2AnimationService.SnapshotPose(_geometry, _pose);

    private void SelectSequence(double position = 0)
    {
        SuspendPlayback(); _elapsedBeforePlay = position; _clock.Reset();
        var sequence = SelectedSequence();
        if (_geometry is null || sequence is null) { _pose = null; _canvas.SetPose(null); return; }
        _updatingTimeline = true; _timeline.Maximum = Math.Max(1, sequence.DurationMilliseconds); _timeline.Value = position; _updatingTimeline = false;
        _pose ??= M2AnimationService.CreatePose(_geometry);
        ToolTip.SetTip(_sequences, $"Animation {sequence.AnimationId}, variant {sequence.SubAnimationId + 1}");
        Sample(position);
        UpdatePlaybackAvailability();
        ResumePlayback();
    }

    private void TogglePlayback()
    {
        _playRequested = !_playRequested;
        if (_playRequested && _loop.IsChecked != true && SelectedSequence() is { } sequence && _elapsedBeforePlay >= sequence.DurationMilliseconds)
            _elapsedBeforePlay = 0;
        if (_playRequested) ResumePlayback(); else SuspendPlayback();
        UpdatePlaybackAvailability();
    }

    private void Tick()
    {
        if (!IsEffectivelyVisible) return;
        var sequence = SelectedSequence(); if (_geometry is null || sequence is null || _pose is null) { SuspendPlayback(); return; }
        var elapsed = CurrentElapsed();
        if (_loop.IsChecked == true) elapsed %= Math.Max(1, sequence.DurationMilliseconds);
        else if (elapsed >= sequence.DurationMilliseconds)
        {
            elapsed = sequence.DurationMilliseconds; _playRequested = false; SuspendPlayback(); _elapsedBeforePlay = elapsed;
        }
        Sample(elapsed); UpdatePlaybackAvailability();
    }

    private void Scrub(double value)
    {
        if (_geometry is null || SelectedSequence() is not { } sequence || _pose is null) return;
        _elapsedBeforePlay = value; if (_timer.IsEnabled) _clock.Restart(); else _clock.Reset();
        Sample(value);
    }

    private void Sample(double value)
    {
        if (_geometry is null || SelectedSequence() is not { } sequence || _pose is null) return;
        try
        {
            if (value == sequence.DurationMilliseconds && value > 0) value = Math.BitDecrement(value);
            M2AnimationService.SampleInto(_geometry, sequence.Index, value, _pose); _canvas.SetPose(_pose);
            _updatingTimeline = true; _timeline.Value = _pose.TimeMilliseconds; _updatingTimeline = false;
            _time.Text = $"{_pose.TimeMilliseconds / 1000d:0.00} / {sequence.DurationMilliseconds / 1000d:0.00} s"; _error.IsVisible = false;
        }
        catch (Exception exception)
        {
            SuspendPlayback(); _pose = null; _canvas.SetPose(null); _error.Text = exception.Message; _error.IsVisible = true;
            DesktopCrashLogger.Log("Model animation failed", exception);
        }
    }

    private void AddCameraButton(int? index, string label)
    {
        var button = new ToggleButton { Content = label, Tag = index, Margin = new Thickness(4, 0, 0, 0), Background = Brush.Parse("#1B2230"), Foreground = Brush.Parse("#E8EBF2"), Padding = new Thickness(8, 4) };
        button.Click += (_, _) => _canvas.SetCamera(index); _cameraButtons.Add(button); _cameraBar.Children.Add(button);
    }

    private void UpdateCameraButtons()
    {
        foreach (var button in _cameraButtons)
        {
            button.IsChecked = (int?)button.Tag == _canvas.CameraIndex;
            button.BorderBrush = button.IsChecked == true ? Brush.Parse("#4FC9A9") : Brush.Parse("#303A4D"); button.BorderThickness = new Thickness(1);
        }
    }

    private M2PreviewSequence? SelectedSequence() => _sequences.SelectedItem as M2PreviewSequence;
    private double CurrentElapsed() => _elapsedBeforePlay + _clock.Elapsed.TotalMilliseconds;
    private void SuspendPlayback() { if (_clock.IsRunning) _elapsedBeforePlay = CurrentElapsed(); _timer.Stop(); _clock.Reset(); }
    private void ResumePlayback() { if (!_playRequested || _geometry is null || _pose is null) return; _clock.Restart(); _timer.Start(); }
    private void UpdatePlaybackAvailability()
    {
        _play.IsEnabled = _geometry is not null && _pose is not null;
        _timeline.IsEnabled = _play.IsEnabled;
        _play.Content = _playRequested ? "\u23F8" : "\u25B6"; ToolTip.SetTip(_play, _playRequested ? "Pause" : "Play");
    }

    public void Dispose() { _playRequested = false; SuspendPlayback(); _canvas.Dispose(); _geometry = null; _pose = null; }
}

internal sealed class M2PreviewCanvas : Control, IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> LoggedParticleFailures = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> LoggedRibbonFailures = new(StringComparer.Ordinal);
    private sealed record MountedModel(M2PreviewGeometry Geometry, Matrix4x4 Transform, PreviewBitmap? Texture, string Label, int? ParentAttachmentIndex);
    private M2PreviewGeometry? _geometry;
    private PreviewBitmap? _texture;
    private readonly Dictionary<int, PreviewBitmap> _materialTextures = [];
    private readonly Dictionary<string, PreviewBitmap> _particleCompositeTextures = new(StringComparer.Ordinal);
    private readonly List<MountedModel> _mountedModels = [];
    private readonly ModelPreviewCamera _camera = new();
    private Avalonia.Point? _dragStart;
    private MouseButton _dragButton;
    private bool _showAttachments;
    private int? _highlightedAttachmentIndex;
    private M2AnimationPose? _pose;
    private int? _nativeCameraIndex;
    private Matrix4x4 _sceneTransform = Matrix4x4.Identity;
    private string? _sceneTransformLabel;
    public int? CameraIndex => _nativeCameraIndex;
    public event EventHandler? CameraChanged;

    public M2PreviewCanvas() => ClipToBounds = true;

    public void SetGeometry(M2PreviewGeometry geometry, bool resetCamera = true)
    {
        _geometry = geometry;
        _pose = null;
        if (resetCamera)
        {
            ClearMaterialTextures(); ClearMountedModels();
            _camera.Frame(geometry.Minimum, geometry.Maximum); _sceneTransform = Matrix4x4.Identity;
            _sceneTransformLabel = null; _nativeCameraIndex = null;
        }
        InvalidateVisual();
    }

    public void ClearGeometry()
    {
        _geometry = null;
        _pose = null;
        ClearMaterialTextures();
        ClearMountedModels();
        InvalidateVisual();
    }

    public void SetTexture(string? previewPath)
    {
        _texture?.Dispose(); _texture = null;
        if (!string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath)) _texture = PreviewBitmap.Decode(previewPath);
        InvalidateVisual();
    }

    public void SetDecodedTexture(RgbaTexture? texture)
    {
        ClearMaterialTextures();
        _texture?.Dispose(); _texture = null;
        if (texture is not null) _texture = PreviewBitmap.Create(texture);
        InvalidateVisual();
    }

    public void SetDecodedTextures(IReadOnlyDictionary<int, RgbaTexture> textures)
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        foreach (var (textureDefinitionIndex, texture) in textures) _materialTextures[textureDefinitionIndex] = PreviewBitmap.Create(texture);
        if (_geometry is not null)
            foreach (var indices in _geometry.ParticleEmitters.Where(emitter => emitter.UsesMultipleTextures).Select(emitter => emitter.TextureDefinitionIndices).DistinctBy(ParticleTextureKey))
            {
                if (indices.Any(index => !textures.ContainsKey(index))) continue;
                try { _particleCompositeTextures[ParticleTextureKey(indices)] = PreviewBitmap.Create(M2ParticleTextureCompositionService.Compose(indices.Select(index => textures[index]).ToArray())); }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
                {
                    DesktopCrashLogger.Log($"M2 multi-texture particle composition unavailable: {_geometry.ModelPath} [{ParticleTextureKey(indices)}]", exception);
                }
            }
        InvalidateVisual();
    }

    public void SetAttachmentOverlay(bool visible, int? highlightedAttachmentIndex = null)
    {
        _showAttachments = visible;
        _highlightedAttachmentIndex = highlightedAttachmentIndex;
        InvalidateVisual();
    }

    public void SetSceneTransform(Matrix4x4 transform, string? label = null)
    {
        if (!Finite(transform)) throw new ArgumentException("The M2 scene transform must contain only finite values.", nameof(transform));
        _sceneTransform = transform;
        _sceneTransformLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        InvalidateVisual();
    }

    public void SetMountedModels(IEnumerable<M2PreviewMountedModel> models)
    {
        ClearMountedModels();
        foreach (var model in models)
        {
            ArgumentNullException.ThrowIfNull(model.Geometry);
            if (!Finite(model.Transform)) throw new ArgumentException($"Mounted model '{model.Label}' has a non-finite transform.", nameof(models));
            if (model.ParentAttachmentIndex is { } attachmentIndex && (_geometry is null || (uint)attachmentIndex >= (uint)_geometry.Attachments.Count))
                throw new ArgumentException($"Mounted model '{model.Label}' references missing parent attachment record {attachmentIndex:N0}.", nameof(models));
            _mountedModels.Add(new(model.Geometry, model.Transform, model.Texture is null ? null : PreviewBitmap.Create(model.Texture), model.Label, model.ParentAttachmentIndex));
        }
        InvalidateVisual();
    }

    public void ClearMountedModels()
    {
        foreach (var model in _mountedModels) model.Texture?.Dispose();
        _mountedModels.Clear();
        InvalidateVisual();
    }

    public void SetPose(M2AnimationPose? pose) { _pose = pose; InvalidateVisual(); }
    public void SetCamera(int? cameraIndex)
    {
        if (cameraIndex is { } index && (_geometry is null || (uint)index >= (uint)_geometry.Cameras.Count)) throw new ArgumentOutOfRangeException(nameof(cameraIndex));
        _nativeCameraIndex = cameraIndex; CameraChanged?.Invoke(this, EventArgs.Empty); InvalidateVisual();
    }

    public void FrameModel()
    {
        if (_geometry is null) return;
        _camera.Frame(_geometry.Minimum, _geometry.Maximum); SetCamera(null);
    }

    public void FocusFace()
    {
        if (_geometry is null) return;
        var vertices = _pose?.Vertices ?? _geometry.Vertices;
        var indices = _geometry.Submeshes.Where(section => section.GeosetGroup == 32 && section.Visible)
            .SelectMany(section => Enumerable.Range(section.TriangleStart, section.TriangleIndexCount)).Select(index => _geometry.TriangleIndices[index]).Distinct().ToArray();
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var index in indices) { min = Vector3.Min(min, vertices[index]); max = Vector3.Max(max, vertices[index]); }
        if (indices.Length == 0)
        {
            var extent = _geometry.Maximum - _geometry.Minimum; min = _geometry.Minimum + new Vector3(0, 0, extent.Z * 0.75f); max = _geometry.Maximum;
        }
        _camera.Focus(min, max); SetCamera(null);
    }

    private void ClearMaterialTextures()
    {
        foreach (var texture in _materialTextures.Values) texture.Dispose();
        _materialTextures.Clear();
        foreach (var texture in _particleCompositeTextures.Values) texture.Dispose();
        _particleCompositeTextures.Clear();
    }

    private static string ParticleTextureKey(IReadOnlyList<int> indices) => string.Join(',', indices);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    public void Dispose()
    {
        _texture?.Dispose(); _texture = null;
        ClearMaterialTextures();
        ClearMountedModels();
        _geometry = null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#090D14")), Bounds);
        if (_geometry is null) return;
        context.Custom(CaptureFrame());
    }

    internal ICustomDrawOperation CaptureFrame() => new M2DrawOperation(Bounds, _geometry!, _pose, _texture, _materialTextures, _particleCompositeTextures, _mountedModels, _sceneTransform, _sceneTransformLabel, _camera.State, _showAttachments, _highlightedAttachmentIndex, _nativeCameraIndex);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        _dragButton = properties.IsMiddleButtonPressed ? MouseButton.Middle : properties.IsRightButtonPressed ? MouseButton.Right : properties.IsLeftButtonPressed ? MouseButton.Left : MouseButton.None;
        if (_dragButton == MouseButton.None) return;
        SetCamera(null);
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start) return;
        var current = e.GetPosition(this);
        var dx = (float)(current.X - start.X); var dy = (float)(current.Y - start.Y);
        if (_dragButton == MouseButton.Left) _camera.Orbit(dx, dy);
        else if (_dragButton == MouseButton.Right) _camera.PanTarget(dx, dy, (float)Bounds.Width, (float)Bounds.Height, _sceneTransform);
        else if (_dragButton == MouseButton.Middle) _camera.MoveModel(dx, dy, (float)Bounds.Width, (float)Bounds.Height, _sceneTransform);
        _dragStart = current;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        _dragButton = MouseButton.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e); _dragStart = null; _dragButton = MouseButton.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetCamera(null); _camera.Zoom((float)e.Delta.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    private sealed class M2DrawOperation(Rect bounds, M2PreviewGeometry geometry, M2AnimationPose? sourcePose, PreviewBitmap? sourceTexture, IReadOnlyDictionary<int, PreviewBitmap> sourceMaterialTextures, IReadOnlyDictionary<string, PreviewBitmap> sourceParticleCompositeTextures, IReadOnlyList<MountedModel> sourceMountedModels, Matrix4x4 sceneTransform, string? sceneTransformLabel, ModelPreviewCameraState camera, bool showAttachments, int? highlightedAttachmentIndex, int? nativeCameraIndex) : ICustomDrawOperation
    {
        private readonly M2AnimationPose? _pose = sourcePose is null ? null : M2AnimationService.SnapshotPose(geometry, sourcePose);
        private readonly PreviewBitmap? _texture = sourceTexture?.Retain();
        private readonly IReadOnlyDictionary<int, PreviewBitmap> _materialTextures = sourceMaterialTextures.ToDictionary(pair => pair.Key, pair => pair.Value.Retain());
        private readonly IReadOnlyDictionary<string, PreviewBitmap> _particleCompositeTextures = sourceParticleCompositeTextures.ToDictionary(pair => pair.Key, pair => pair.Value.Retain(), StringComparer.Ordinal);
        private readonly IReadOnlyList<MountedModel> _mountedModels = sourceMountedModels.Select(model => model with { Texture = model.Texture?.Retain() }).ToArray();
        private sealed record SceneSource(M2PreviewGeometry Geometry, Matrix4x4 Transform, PreviewBitmap? ManualTexture, IReadOnlyDictionary<int, PreviewBitmap>? MaterialTextures, string Label, IReadOnlyList<Vector3>? PosedVertices, IReadOnlyList<Vector3>? PosedNormals, Vector3 Minimum, Vector3 Maximum);
        public Rect Bounds => bounds;
        public bool HitTest(Avalonia.Point point) => Bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose()
        {
            _texture?.Dispose();
            foreach (var texture in _materialTextures.Values) texture.Dispose();
            foreach (var texture in _particleCompositeTextures.Values) texture.Dispose();
            foreach (var model in _mountedModels) model.Texture?.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null) return;
            var pose = _pose; var texture = _texture?.Bitmap;
            var materialTextures = _materialTextures; var particleCompositeTextures = _particleCompositeTextures; var mountedModels = _mountedModels;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var width = (float)bounds.Width;
            var height = (float)bounds.Height;
            if (width <= 0 || height <= 0) return;
            var sources = new List<SceneSource>(mountedModels.Count + 1) { new(geometry, Matrix4x4.Identity, _texture, materialTextures, Path.GetFileName(geometry.ModelPath), pose?.Vertices, pose?.Normals, pose?.Minimum ?? geometry.Minimum, pose?.Maximum ?? geometry.Maximum) };
            foreach (var model in mountedModels)
            {
                var transform = model.Transform;
                if (pose is not null && model.ParentAttachmentIndex is { } attachmentIndex)
                {
                    var attachment = geometry.Attachments[attachmentIndex];
                    transform = model.Transform * pose.BoneTransforms[attachment.BoneIndex];
                }
                sources.Add(new SceneSource(model.Geometry, transform, model.Texture, null, model.Label, null, null, model.Geometry.Minimum, model.Geometry.Maximum));
            }
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            foreach (var source in sources)
            {
                var transformedBounds = M2PreviewSceneService.TransformBounds(source.Minimum, source.Maximum, source.Transform);
                minimum = Vector3.Min(minimum, transformedBounds.Minimum); maximum = Vector3.Max(maximum, transformedBounds.Maximum);
            }
            var center = camera.Target - camera.ModelOffset;
            var extent = maximum - minimum;
            var largest = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
            if (!float.IsFinite(largest) || largest <= 0.00001f) return;

            var orbitScale = camera.Scale(width, height);
            var orbitRotation = sceneTransform * camera.Rotation;
            var useNativeCamera = nativeCameraIndex is { } requestedCamera && (uint)requestedCamera < (uint)geometry.Cameras.Count;
            M2PreviewCamera? nativeCamera = useNativeCamera ? geometry.Cameras[nativeCameraIndex!.Value] : null;
            var cameraPose = nativeCamera is null ? null : pose is not null && nativeCamera.Index < pose.Cameras.Length ? pose.Cameras[nativeCamera.Index] : new M2PreviewCameraPose(nativeCamera.BasePosition, nativeCamera.BaseTarget, 0);
            var cameraProjection = nativeCamera is not null && cameraPose is not null ? M2CameraProjectionService.TryCreate(nativeCamera, cameraPose, sceneTransform) : null;
            useNativeCamera = cameraProjection is not null;
            var scale = useNativeCamera ? height * 0.5f : orbitScale;
            var triangleCount = sources.Sum(source => (source.Geometry.Batches.Count == 0 ? source.Geometry.TriangleIndices.Count : source.Geometry.Batches.Sum(batch => batch.TriangleIndexCount)) / 3);
            var faces = new List<DepthBufferedMesh.Triangle>(triangleCount);
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var source = sources[sourceIndex]; var sourceGeometry = source.Geometry; var sourceVertices = source.PosedVertices ?? sourceGeometry.Vertices; var sourceNormals = source.PosedNormals ?? sourceGeometry.Normals; var transformed = new Vector3[sourceVertices.Count]; var viewVertices = new Vector3[sourceVertices.Count]; var transformedNormals = new Vector3[sourceNormals.Count];
                for (var index = 0; index < transformed.Length; index++)
                {
                    if (useNativeCamera)
                    {
                        var world = Vector3.Transform(Vector3.Transform(sourceVertices[index], source.Transform), sceneTransform); var view = cameraProjection!.ToViewPoint(world); viewVertices[index] = view; transformed[index] = cameraProjection.Project(view);
                    }
                    else transformed[index] = viewVertices[index] = Vector3.Transform(Vector3.Transform(sourceVertices[index], source.Transform) - center, orbitRotation);
                }
                for (var index = 0; index < transformedNormals.Length; index++)
                {
                    var sourceNormal = Vector3.TransformNormal(sourceNormals[index], source.Transform);
                    Vector3 normal;
                    if (useNativeCamera) { var worldNormal = Vector3.TransformNormal(sourceNormal, sceneTransform); normal = cameraProjection!.ToViewNormal(worldNormal); }
                    else normal = Vector3.TransformNormal(sourceNormal, orbitRotation);
                    transformedNormals[index] = normal.LengthSquared() > 0.0000001f && float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z) ? Vector3.Normalize(normal) : Vector3.UnitZ;
                }
                var activeLights = new List<SceneLight>();
                if (sourceIndex == 0 && pose is not null)
                    for (var index = 0; index < Math.Min(geometry.Lights.Count, pose.Lights.Length); index++)
                    {
                        var value = pose.Lights[index]; Vector3 lightPosition; Vector3 lightDirection;
                        if (useNativeCamera)
                        {
                            lightPosition = cameraProjection!.ToViewPoint(Vector3.Transform(value.Position, sceneTransform)); lightDirection = cameraProjection.ToViewNormal(Vector3.TransformNormal(value.Direction, sceneTransform));
                        }
                        else { lightPosition = Vector3.Transform(value.Position - center, orbitRotation); lightDirection = Vector3.TransformNormal(value.Direction, orbitRotation); }
                        if (lightDirection.LengthSquared() > 0.0000001f) lightDirection = Vector3.Normalize(lightDirection);
                        activeLights.Add(new(geometry.Lights[index].Type, value with { Position = lightPosition, Direction = lightDirection }));
                    }
                IReadOnlyList<M2PreviewBatch> batches = sourceGeometry.Batches.Count == 0 ? [new M2PreviewBatch(0, 0, 0, sourceGeometry.TriangleIndices.Count, null, null)] : sourceGeometry.Batches;
                var firstBatchBySubmesh = batches.GroupBy(batch => batch.SubmeshIndex).ToDictionary(group => group.Key, group => group.First());
                var texturedSubmeshes = batches.Where(batch => ResolveTextureStages(source, batch).Count > 0).Select(batch => batch.SubmeshIndex).ToHashSet();
                foreach (var batch in batches)
                {
                    var firstPass = ReferenceEquals(batch, firstBatchBySubmesh[batch.SubmeshIndex]);
                    if (source.ManualTexture is not null && !firstPass) continue;
                    var end = Math.Min(sourceGeometry.TriangleIndices.Count, batch.TriangleStart + batch.TriangleIndexCount);
                    var activeStages = ResolveTextureStages(source, batch);
                    if (activeStages.Count == 0 && (texturedSubmeshes.Contains(batch.SubmeshIndex) || !firstPass)) continue;
                    var passOrder = batch.PriorityPlane * 131_072 + (batch.MaterialUnitIndex ?? 0);
                    var material = new DepthBufferedMesh.Material(activeStages.Select(stage => new DepthBufferedMesh.Texture(stage.Texture.Pixels,
                        stage.Texture.Bitmap.Width, stage.Texture.Bitmap.Height, (stage.Flags & 1) != 0, (stage.Flags & 2) != 0, stage.CoordinateSource, stage.Blend)).ToArray(),
                        activeStages.Count > 1 ? M2TextureCombinerRenderPlanService.Build(batch.Combiner, batch.TextureStages) : [], batch.BlendMode, (batch.RenderFlags & 0x10) == 0, passOrder);
                    for (var offset = batch.TriangleStart; offset + 2 < end; offset += 3)
                    {
                        var ia = sourceGeometry.TriangleIndices[offset]; var ib = sourceGeometry.TriangleIndices[offset + 1]; var ic = sourceGeometry.TriangleIndices[offset + 2];
                        var a = transformed[ia]; var b = transformed[ib]; var c = transformed[ic];
                        if (useNativeCamera && (!cameraProjection!.ContainsDepth(a.Y) || !cameraProjection.ContainsDepth(b.Y) || !cameraProjection.ContainsDepth(c.Y))) continue;
                        var ax = width * 0.5f + a.X * scale; var ay = height * 0.5f - a.Z * scale;
                        var bx = width * 0.5f + b.X * scale; var by = height * 0.5f - b.Z * scale;
                        var cx = width * 0.5f + c.X * scale; var cy = height * 0.5f - c.Z * scale;
                        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                        if (Math.Abs(area) < 0.02f) continue;
                        if (area >= 0 && (batch.RenderFlags & 0x4) == 0) continue;
                        var viewA = viewVertices[ia]; var viewB = viewVertices[ib]; var viewC = viewVertices[ic];
                        var lightingA = (batch.RenderFlags & 0x1) != 0 ? Vector3.One : SceneLighting(transformedNormals[ia], viewA, activeLights);
                        var lightingB = (batch.RenderFlags & 0x1) != 0 ? Vector3.One : SceneLighting(transformedNormals[ib], viewB, activeLights);
                        var lightingC = (batch.RenderFlags & 0x1) != 0 ? Vector3.One : SceneLighting(transformedNormals[ic], viewC, activeLights);
                        var edgeFadeA = 1f; var edgeFadeB = 1f; var edgeFadeC = 1f;
                        if (batch.Combiner.Kind == M2PreviewTextureCombinerKind.ExplicitModModEdgeFade)
                        {
                            edgeFadeA = M2EdgeFadeService.Opacity(transformedNormals[ia], useNativeCamera ? -viewA : Vector3.UnitY); edgeFadeB = M2EdgeFadeService.Opacity(transformedNormals[ib], useNativeCamera ? -viewB : Vector3.UnitY); edgeFadeC = M2EdgeFadeService.Opacity(transformedNormals[ic], useNativeCamera ? -viewC : Vector3.UnitY);
                        }
                        faces.Add(new(Vertex(ia, ax, ay, viewA.Y, lightingA, edgeFadeA), Vertex(ib, bx, by, viewB.Y, lightingB, edgeFadeB), Vertex(ic, cx, cy, viewC.Y, lightingC, edgeFadeC), material));
                    }

                    DepthBufferedMesh.Vertex Vertex(int index, float x, float y, float depth, Vector3 lighting, float edgeFade) =>
                        new(new(x, y, depth), useNativeCamera ? 1 / depth : 1, sourceGeometry.TextureCoordinates[index],
                            sourceGeometry.SecondaryTextureCoordinates.Count == sourceGeometry.Vertices.Count ? sourceGeometry.SecondaryTextureCoordinates[index] : sourceGeometry.TextureCoordinates[index],
                            M2EnvironmentMapService.Coordinate(transformedNormals[index]), lighting, edgeFade);
                }
            }
            var matrix = canvas.TotalMatrix;
            var pixelScale = Math.Max(1, MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY));
            using (var mesh = DepthBufferedMesh.Render(checked((int)MathF.Ceiling(width * pixelScale)), checked((int)MathF.Ceiling(height * pixelScale)), faces, pixelScale))
                canvas.DrawBitmap(mesh, new SKRect(0, 0, width, height));

            string? ribbonFailure = null;
            var displayedRibbonSections = 0;
            var ribbonEmitterCount = sources.Sum(source => source.Geometry.RibbonEmitters.Count);
            for (var effectSourceIndex = 0; effectSourceIndex < sources.Count; effectSourceIndex++)
            {
                var effectSource = sources[effectSourceIndex]; IReadOnlyList<M2PreviewRibbonTrail> ribbonTrails;
                try { ribbonTrails = M2RibbonPreviewService.BuildTrails(effectSource.Geometry, effectSourceIndex == 0 ? pose : null); }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    ribbonTrails = []; ribbonFailure ??= exception.Message;
                    var key = effectSource.Geometry.ModelPath + "\0" + exception.Message;
                    if (LoggedRibbonFailures.TryAdd(key, 0)) DesktopCrashLogger.Log($"M2 ribbon preview unavailable: {effectSource.Geometry.ModelPath}", exception);
                }
                foreach (var trail in ribbonTrails)
                {
                    if (trail.Sections.Count < 2) continue;
                    var positions = new SKPoint[trail.Sections.Count * 2]; var coordinates = new SKPoint[positions.Length]; var colors = new SKColor[positions.Length]; var visible = true;
                    var tint = new SKColor(Channel(trail.Color.X * 255), Channel(trail.Color.Y * 255), Channel(trail.Color.Z * 255), Channel(trail.Color.W * 255));
                    for (var sectionIndex = 0; sectionIndex < trail.Sections.Count; sectionIndex++)
                    {
                        var section = trail.Sections[sectionIndex]; AddPoint(section.Center + section.Up * section.Above, sectionIndex * 2, section.TextureU, 0); AddPoint(section.Center - section.Up * section.Below, sectionIndex * 2 + 1, section.TextureU, 1);
                    }
                    if (!visible) continue;
                    var blend = trail.BlendMode switch { 3 or 4 => SKBlendMode.Plus, 5 or 6 => SKBlendMode.Modulate, _ => SKBlendMode.SrcOver };
                    using var ribbonPaint = new SKPaint { IsAntialias = true, BlendMode = blend, Color = tint };
                    var ribbonTexture = effectSource.ManualTexture?.Bitmap;
                    if (ribbonTexture is null && effectSource.MaterialTextures?.TryGetValue(trail.TextureDefinitionIndex, out var ribbonBitmap) == true) ribbonTexture = ribbonBitmap.Bitmap;
                    if (ribbonTexture is not null)
                    {
                        for (var index = 0; index < coordinates.Length; index++) coordinates[index] = new(coordinates[index].X * ribbonTexture.Width, coordinates[index].Y * ribbonTexture.Height);
                        using var texturedMesh = SKVertices.CreateCopy(SKVertexMode.TriangleStrip, positions, coordinates, colors); using var shader = SKShader.CreateBitmap(ribbonTexture, SKShaderTileMode.Repeat, SKShaderTileMode.Clamp);
                        ribbonPaint.Shader = shader; canvas.DrawVertices(texturedMesh, SKBlendMode.Modulate, ribbonPaint);
                    }
                    else { using var ribbonMesh = SKVertices.CreateCopy(SKVertexMode.TriangleStrip, positions, coordinates, colors); canvas.DrawVertices(ribbonMesh, SKBlendMode.Src, ribbonPaint); }
                    displayedRibbonSections += trail.Sections.Count;

                    void AddPoint(Vector3 sourcePoint, int destination, float u, float v)
                    {
                        var mountedPoint = Vector3.Transform(sourcePoint, effectSource.Transform); Vector3 point;
                        if (useNativeCamera)
                        {
                            var view = cameraProjection!.ToViewPoint(Vector3.Transform(mountedPoint, sceneTransform));
                            if (!cameraProjection.ContainsDepth(view.Y)) { visible = false; return; }
                            point = cameraProjection.Project(view);
                        }
                        else point = Vector3.Transform(mountedPoint - center, orbitRotation);
                        positions[destination] = new(width * 0.5f + point.X * scale, height * 0.5f - point.Z * scale); coordinates[destination] = new(u, v); colors[destination] = tint;
                    }
                }
            }

            IReadOnlyList<M2PreviewParticleSprite> particleSprites;
            string? particleFailure = null;
            var projectedParticles = new List<ProjectedParticle>(); var particleEmitterCount = sources.Sum(source => source.Geometry.ParticleEmitters.Count);
            for (var effectSourceIndex = 0; effectSourceIndex < sources.Count; effectSourceIndex++)
            {
                var effectSource = sources[effectSourceIndex];
                try { particleSprites = M2ParticlePreviewService.BuildSprites(effectSource.Geometry, effectSourceIndex == 0 ? pose : null); }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    particleSprites = []; particleFailure ??= exception.Message;
                    var key = effectSource.Geometry.ModelPath + "\0" + exception.Message;
                    if (LoggedParticleFailures.TryAdd(key, 0)) DesktopCrashLogger.Log($"M2 particle preview unavailable: {effectSource.Geometry.ModelPath}", exception);
                }
                foreach (var particle in particleSprites)
                {
                    var mountedPosition = Vector3.Transform(particle.Position, effectSource.Transform); Vector3 point;
                    if (useNativeCamera) { var view = cameraProjection!.ToViewPoint(Vector3.Transform(mountedPosition, sceneTransform)); if (!cameraProjection.ContainsDepth(view.Y)) continue; point = cameraProjection.Project(view); }
                    else point = Vector3.Transform(mountedPosition - center, orbitRotation);
                    var x = width * 0.5f + point.X * scale; var y = height * 0.5f - point.Z * scale; var radiusX = Math.Clamp(particle.Width * scale, 0.5f, 256f); var radiusY = Math.Clamp(particle.Height * scale, 0.5f, 256f);
                    if (x + radiusX < 0 || x - radiusX > width || y + radiusY < 0 || y - radiusY > height) continue;
                    projectedParticles.Add(new(point.Y, x, y, radiusX, radiusY, effectSourceIndex, particle));
                }
            }
            projectedParticles.Sort(static (left, right) => right.Depth.CompareTo(left.Depth));
            for (var start = 0; start < projectedParticles.Count;)
            {
                var keySource = projectedParticles[start].SourceIndex; var keyTextures = ParticleTextureKey(projectedParticles[start].Sprite.TextureDefinitionIndices); var keyTexture = projectedParticles[start].Sprite.TextureDefinitionIndex; var keyBlend = projectedParticles[start].Sprite.BlendMode; var end = start + 1;
                while (end < projectedParticles.Count && projectedParticles[end].SourceIndex == keySource && ParticleTextureKey(projectedParticles[end].Sprite.TextureDefinitionIndices) == keyTextures && projectedParticles[end].Sprite.BlendMode == keyBlend) end++;
                var blend = keyBlend switch { 3 or 4 => SKBlendMode.Plus, 5 or 6 => SKBlendMode.Modulate, _ => SKBlendMode.SrcOver };
                using var particlePaint = new SKPaint { IsAntialias = true, BlendMode = blend };
                var particleSource = sources[keySource]; var particleTexture = particleSource.ManualTexture?.Bitmap;
                if (particleTexture is null && projectedParticles[start].Sprite.TextureDefinitionIndices.Count > 1)
                {
                    if (keySource == 0 && particleCompositeTextures.TryGetValue(keyTextures, out var compositeBitmap)) particleTexture = compositeBitmap.Bitmap;
                    if (particleTexture is null) particleFailure ??= $"Multi-texture particle {projectedParticles[start].Sprite.EmitterIndex:N0} requires texture definitions {keyTextures}; at least one decoded layer is unavailable.";
                }
                if (particleTexture is null && particleSource.MaterialTextures?.TryGetValue(keyTexture, out var particleBitmap) == true) particleTexture = particleBitmap.Bitmap;
                if (particleTexture is not null)
                {
                    var count = end - start; var positions = new SKPoint[count * 6]; var coordinates = new SKPoint[count * 6]; var colors = new SKColor[count * 6];
                    for (var runIndex = 0; runIndex < count; runIndex++)
                    {
                        var projected = projectedParticles[start + runIndex]; var particle = projected.Sprite; var offset = runIndex * 6;
                        var cosine = MathF.Cos(particle.Rotation); var sine = MathF.Sin(particle.Rotation); var radiusX = projected.RadiusX; var radiusY = projected.RadiusY;
                        var a = Rotate(-radiusX, -radiusY); var b = Rotate(radiusX, -radiusY); var c = Rotate(radiusX, radiusY); var d = Rotate(-radiusX, radiusY);
                        positions[offset] = a; positions[offset + 1] = b; positions[offset + 2] = c; positions[offset + 3] = a; positions[offset + 4] = c; positions[offset + 5] = d;
                        var columns = Math.Max(1, (int)particle.Columns); var rows = Math.Max(1, (int)particle.Rows); var tileWidth = particleTexture.Width / (float)columns; var tileHeight = particleTexture.Height / (float)rows;
                        var column = particle.TileIndex % columns; var row = particle.TileIndex / columns; var left = column * tileWidth; var top = row * tileHeight; var right = left + tileWidth; var bottom = top + tileHeight;
                        coordinates[offset] = new(left, top); coordinates[offset + 1] = new(right, top); coordinates[offset + 2] = new(right, bottom); coordinates[offset + 3] = new(left, top); coordinates[offset + 4] = new(right, bottom); coordinates[offset + 5] = new(left, bottom);
                        var tint = new SKColor(Channel(particle.Color.X * 255), Channel(particle.Color.Y * 255), Channel(particle.Color.Z * 255), Channel(particle.Color.W * 255));
                        for (var vertex = 0; vertex < 6; vertex++) colors[offset + vertex] = tint;
                        SKPoint Rotate(float x, float y) => new(projected.X + x * cosine - y * sine, projected.Y + x * sine + y * cosine);
                    }
                    using var mesh = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, coordinates, colors); using var shader = SKShader.CreateBitmap(particleTexture, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                    particlePaint.Shader = shader; canvas.DrawVertices(mesh, SKBlendMode.Modulate, particlePaint);
                }
                else for (var index = start; index < end; index++) { var projected = projectedParticles[index]; var particle = projected.Sprite; particlePaint.Color = new(Channel(particle.Color.X * 255), Channel(particle.Color.Y * 255), Channel(particle.Color.Z * 255), Channel(particle.Color.W * 255)); canvas.DrawOval(new SKRect(projected.X - projected.RadiusX, projected.Y - projected.RadiusY, projected.X + projected.RadiusX, projected.Y + projected.RadiusY), particlePaint); }
                start = end;
            }

            if (showAttachments && geometry.Attachments.Count > 0)
            {
                using var marker = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(69, 211, 255, 210) };
                using var markerEdge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(6, 14, 24, 230) };
                using var labelBack = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(5, 10, 18, 220) };
                using var labelText = new SKPaint { IsAntialias = true, Color = new SKColor(232, 247, 255) };
                using var labelFont = new SKFont(SKTypeface.Default, 12);
                foreach (var attachment in geometry.Attachments)
                {
                    var attachmentPosition = pose is not null && attachment.Index < pose.AttachmentPositions.Length ? pose.AttachmentPositions[attachment.Index] : attachment.Position; Vector3 point;
                    if (useNativeCamera)
                    {
                        var view = cameraProjection!.ToViewPoint(Vector3.Transform(attachmentPosition, sceneTransform)); if (!cameraProjection.ContainsDepth(view.Y)) continue; point = cameraProjection.Project(view);
                    }
                    else point = Vector3.Transform(attachmentPosition - center, orbitRotation);
                    var x = width * 0.5f + point.X * scale; var y = height * 0.5f - point.Z * scale;
                    var selected = highlightedAttachmentIndex == attachment.Index;
                    var radius = selected ? 6f : 3.2f;
                    marker.Color = selected ? new SKColor(255, 189, 72, 245) : new SKColor(69, 211, 255, 190);
                    canvas.DrawCircle(x, y, radius, marker); canvas.DrawCircle(x, y, radius, markerEdge);
                    if (!selected) continue;
                    var label = $"{attachment.Id:N0} · {attachment.Name} · bone {attachment.BoneIndex:N0}";
                    var widthText = labelFont.MeasureText(label, labelText); var left = Math.Clamp(x + 9, 4, Math.Max(4, width - widthText - 12)); var top = Math.Clamp(y - 21, 4, Math.Max(4, height - 25));
                    canvas.DrawRoundRect(new SKRect(left - 4, top - 2, left + widthText + 4, top + 17), 4, 4, labelBack);
                    canvas.DrawText(label, left, top + 12, SKTextAlign.Left, labelFont, labelText);
                }
            }

            using var text = new SKPaint { IsAntialias = true, Color = new SKColor(225, 231, 240) };
            using var titleFont = new SKFont(SKTypeface.Default, 13);
            var geosets = geometry.Submeshes.Count == 0 ? "complete mesh" : $"{geometry.Submeshes.Count(section => section.Visible):N0}/{geometry.Submeshes.Count:N0} geosets";
            var textureCount = texture is not null ? "manual texture" : $"{materialTextures.Count:N0} material texture(s)";
            var multiTextureUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1);
            var multiTexture = multiTextureUnits == 0 ? string.Empty : $" · {multiTextureUnits:N0} multi-texture unit(s)";
            var approximateUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1 && batch.Combiner.Supported && !batch.Combiner.Exact);
            var approximate = approximateUnits == 0 ? string.Empty : $" · {approximateUnits:N0} approximate";
            var environmentUnits = geometry.Batches.Count(batch => batch.TextureStages.Any(stage => stage.CoordinateSource == M2PreviewTextureCoordinateSource.Environment));
            var environment = environmentUnits == 0 ? string.Empty : $" · {environmentUnits:N0} sphere-map unit(s)";
            var edgeFadeUnits = geometry.Batches.Count(batch => batch.Combiner.Kind == M2PreviewTextureCombinerKind.ExplicitModModEdgeFade);
            var edgeFade = edgeFadeUnits == 0 ? string.Empty : $" · {edgeFadeUnits:N0} edge-fade unit(s)";
            var cameraLabel = useNativeCamera ? $" · {nativeCamera!.Name}" : string.Empty;
            var embeddedLights = geometry.Lights.Count == 0 ? string.Empty : $" · {geometry.Lights.Count:N0} embedded light(s)";
            var particles = particleEmitterCount == 0 ? string.Empty : $" · {projectedParticles.Count:N0}/{particleEmitterCount:N0} particle sprites/emitters";
            var particleFallback = particleFailure is null ? string.Empty : " · particle fallback";
            var multiTextureParticles = sources.Sum(source => source.Geometry.ParticleEmitters.Count(emitter => emitter.UsesMultipleTextures));
            var particleLayers = multiTextureParticles == 0 ? string.Empty : $" · {multiTextureParticles:N0} multi-texture particle emitter(s)";
            var ribbons = ribbonEmitterCount == 0 ? string.Empty : $" · {displayedRibbonSections:N0}/{ribbonEmitterCount:N0} ribbon sections/emitters";
            var ribbonFallback = ribbonFailure is null ? string.Empty : " · ribbon fallback";
            var fallbackUnits = geometry.Batches.Count(batch => batch.TextureStages.Count > 1 && (!batch.Combiner.Supported || batch.TextureStages.Any(stage => stage.CoordinateSource == M2PreviewTextureCoordinateSource.Unsupported)));
            var fallback = fallbackUnits == 0 ? string.Empty : $" · {fallbackUnits:N0} first-stage fallback(s)";
            var attachments = showAttachments ? $" · {geometry.Attachments.Count:N0} attachment point(s)" : string.Empty;
            var mounted = mountedModels.Count == 0 ? string.Empty : $" · {mountedModels.Count:N0} mounted model(s)";
            var animation = pose is null ? string.Empty : $" · {M2AnimationNames.Label(geometry.Sequences[pose.SequenceIndex])}";
            var scene = sceneTransformLabel is null ? string.Empty : $" · {sceneTransformLabel}";
            canvas.DrawText($"{Path.GetFileName(geometry.ModelPath)} · {geosets} · {textureCount}{multiTexture}{environment}{edgeFade}{cameraLabel}{embeddedLights}{ribbons}{ribbonFallback}{particles}{particleLayers}{particleFallback}{approximate}{fallback} · {faces.Count:N0} displayed faces{animation}{attachments}{mounted}{scene}", 12, 23, SKTextAlign.Left, titleFont, text);
            text.Color = new SKColor(170, 182, 200);
        }

        private static IReadOnlyList<ResolvedTextureStage> ResolveTextureStages(SceneSource source, M2PreviewBatch batch)
        {
            if (source.ManualTexture is not null) return [new(source.ManualTexture, M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureStageBlend.Source, 3)];
            if (batch.TextureStages.Count == 0)
                return batch.TextureDefinitionIndex is { } index && source.MaterialTextures?.TryGetValue(index, out var texture) == true
                    ? [new(texture, M2PreviewTextureCoordinateSource.Primary, M2PreviewTextureStageBlend.Source, source.Geometry.TextureSlots.FirstOrDefault(slot => slot.Index == index)?.Flags ?? 0)]
                    : [];
            var result = new List<ResolvedTextureStage>(batch.TextureStages.Count);
            foreach (var stage in batch.TextureStages)
            {
                if (stage.TextureDefinitionIndex < 0 || source.MaterialTextures is not { } textures || !textures.TryGetValue(stage.TextureDefinitionIndex, out var texture)) return result.Count == 0 ? [] : [result[0]];
                if (stage.CoordinateSource == M2PreviewTextureCoordinateSource.Unsupported || stage.Blend == M2PreviewTextureStageBlend.Unsupported)
                    return result.Count == 0 ? [] : [result[0]];
                result.Add(new(texture, stage.CoordinateSource, stage.Blend, source.Geometry.TextureSlots.FirstOrDefault(slot => slot.Index == stage.TextureDefinitionIndex)?.Flags ?? 0));
            }
            return batch.Combiner.Supported ? result : result.Count == 0 ? [] : [result[0]];
        }

        private static Vector3 SceneLighting(Vector3 normal, Vector3 center, IReadOnlyList<SceneLight> lights)
        {
            if (lights.Count == 0)
            {
                var fallback = Vector3.Normalize(new Vector3(-0.35f, -0.65f, 0.9f));
                return new Vector3(Math.Clamp(0.25f + 0.75f * Math.Abs(Vector3.Dot(normal, fallback)), 0.2f, 1f));
            }
            var lighting = new Vector3(0.08f);
            foreach (var light in lights)
            {
                var value = light.Pose; lighting += Vector3.Max(Vector3.Zero, value.AmbientColor) * Math.Max(0, value.AmbientIntensity);
                Vector3 towardLight; var attenuation = 1f;
                if (light.Type == 0) towardLight = -value.Direction;
                else
                {
                    towardLight = value.Position - center; var distance = towardLight.Length(); if (distance <= 0.000001f) towardLight = Vector3.UnitY; else towardLight /= distance;
                    if (value.UseAttenuation && value.AttenuationEnd > value.AttenuationStart) attenuation = 1f - Math.Clamp((distance - value.AttenuationStart) / (value.AttenuationEnd - value.AttenuationStart), 0f, 1f);
                }
                if (towardLight.LengthSquared() > 0.0000001f) towardLight = Vector3.Normalize(towardLight);
                lighting += Vector3.Max(Vector3.Zero, value.DiffuseColor) * (Math.Max(0, value.DiffuseIntensity) * Math.Abs(Vector3.Dot(normal, towardLight)) * attenuation);
            }
            return Vector3.Clamp(lighting, new Vector3(0.12f), Vector3.One);
        }

        private static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);

        private sealed record ResolvedTextureStage(PreviewBitmap Texture, M2PreviewTextureCoordinateSource CoordinateSource, M2PreviewTextureStageBlend Blend, uint Flags);
        private sealed record SceneLight(short Type, M2PreviewLightPose Pose);
        private readonly record struct ProjectedParticle(float Depth, float X, float Y, float RadiusX, float RadiusY, int SourceIndex, M2PreviewParticleSprite Sprite);
    }
}
