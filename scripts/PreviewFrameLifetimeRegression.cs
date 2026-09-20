using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

public static class PreviewFrameLifetimeRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Vector3[] Vertices = [new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)];
    private static readonly Vector3[] Normals = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX];
    private static readonly Vector2[] Uvs = [new(0, 1), new(1, 1), new(0.5f, 0)];
    private static readonly M2PreviewGeometry Model = new("lifetime.m2", "lifetime00.skin", Vertices, Normals, Uvs, [0, 1, 2], new(0, -1, -1), new(0, 1, 1), [new(0, 0, 0, "test.blp")])
    { Batches = [new(0, 0, 0, 3, 0, 0) { RenderFlags = 4 }], UsedTextureDefinitionIndices = [0] };
    private static readonly WmoPreviewGeometry WorldModel = new("lifetime.wmo", 17, Vertices, Normals, Uvs, [], [0, 1, 2],
        [new(0, 0, 0, 0, "test.blp", null, null)], [new(0, 0, 0, 3)], [new(0, "lifetime_000.wmo", 0, 0, 3, 0, 3, 0, 1, new(0, -1, -1), new(0, 1, 1), [])], new(0, -1, -1), new(0, 1, 1), []);

    public static void Run()
    {
        foreach (var mode in new[] { "M2 materials", "M2 manual", "M2 mounted", "M2 particle composites", "WMO materials" })
        {
            for (var cycle = 0; cycle < 100; cycle++) Check(mode);
            Console.WriteLine($"PASS {mode}: 100 queued frames survived texture replacement, geometry clear and view disposal; retired bitmaps released.");
        }
        CheckPose();
        Console.WriteLine("PASS animation frames retain their sampled pose when the UI advances to the next frame.");
        CheckManualSelection();
        Console.WriteLine("PASS manually selected textures remain selected when comparison geometry is replaced.");
        CheckInteraction();
        CheckDepth();
        CheckSavedDefaults();
    }

    private static void Check(string mode)
    {
        using var owner = mode.StartsWith("WMO") ? (IDisposable)new WmoPreviewView() : new M2PreviewView();
        var canvas = (Control)owner.GetType().GetField("_canvas", Fields).GetValue(owner);
        canvas.Measure(new Size(256, 256)); canvas.Arrange(new Rect(0, 0, 256, 256));
        var red = new RgbaTexture(1, 1, [220, 30, 20, 255]);
        var green = new RgbaTexture(1, 1, [20, 220, 30, 255]);
        Set(red);
        using var operation = Capture(canvas);
        using var secondFrame = Capture(canvas);
        var before = Bitmaps(operation);
        if (before.Length == 0) throw new InvalidOperationException(mode + ": no texture captured.");
        if (mode == "M2 particle composites" && before.Length != 3) throw new InvalidOperationException("Particle composite texture was not captured with its source layers.");
        var expected = Render(operation);
        Set(green);
        using (var replacement = Capture(canvas))
            if (expected.SequenceEqual(Render(replacement))) throw new InvalidOperationException(mode + ": changing the texture did not change rendered pixels.");
        if (owner is M2PreviewView m2) m2.ClearGeometry(); else ((WmoPreviewView)owner).ClearGeometry();
        owner.Dispose();
        // Check before calling native code so the broken build fails without an access violation.
        if (before.Any(bitmap => bitmap.Handle == IntPtr.Zero))
            throw new InvalidOperationException(mode + ": replacing a texture disposed a bitmap still owned by a queued frame.");
        if (!before.ToHashSet().SetEquals(Bitmaps(operation)))
            throw new InvalidOperationException(mode + ": a queued frame retained a mutable texture collection.");
        var actual = Task.Run(() => Render(operation)).GetAwaiter().GetResult();
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(mode + ": a queued frame changed after its view was cleared.");
        operation.Dispose();
        if (before.Any(bitmap => bitmap.Handle == IntPtr.Zero))
            throw new InvalidOperationException(mode + ": retiring one frame invalidated another queued frame.");
        if (!expected.SequenceEqual(Task.Run(() => Render(secondFrame)).GetAwaiter().GetResult()))
            throw new InvalidOperationException(mode + ": the second queued frame changed after the first retired.");
        secondFrame.Dispose();
        if (before.Any(bitmap => bitmap.Handle != IntPtr.Zero))
            throw new InvalidOperationException(mode + ": the retired frame leaked a native bitmap.");

        void Set(RgbaTexture texture)
        {
            if (owner is WmoPreviewView wmo) { wmo.SetGeometry(WorldModel); wmo.SetDecodedTextures(new Dictionary<int, RgbaTexture> { [0] = texture }); }
            else
            {
                var preview = (M2PreviewView)owner;
                preview.SetGeometry(mode == "M2 particle composites" ? Model with
                {
                    ParticleEmitters = [new(0, 0, Vector3.Zero, -1, 0, 0, 1, 1, 1, [Vector4.One], [1], 0) { TextureDefinitionIndices = [0, 1] }]
                } : Model);
                if (mode == "M2 manual") preview.SetDecodedTexture(texture);
                else if (mode == "M2 mounted") preview.SetMountedModels([new(Model, Matrix4x4.Identity, texture, "mounted")]);
                else preview.SetDecodedTextures(mode == "M2 particle composites"
                    ? new Dictionary<int, RgbaTexture> { [0] = texture, [1] = texture }
                    : new Dictionary<int, RgbaTexture> { [0] = texture });
            }
        }
    }

    private static void CheckPose()
    {
        using var preview = new M2PreviewView(); preview.SetGeometry(Model);
        var canvas = (Control)preview.GetType().GetField("_canvas", Fields).GetValue(preview);
        var pose = M2AnimationService.CreatePose(Model);
        typeof(M2AnimationPose).GetProperty("SequenceIndex").SetValue(pose, 0);
        Model.Vertices.ToArray().CopyTo(pose.Vertices, 0); Model.Normals.ToArray().CopyTo(pose.Normals, 0);
        canvas.GetType().GetMethod("SetPose").Invoke(canvas, [pose]);
        using var operation = Capture(canvas);
        var captured = Objects(operation).OfType<M2AnimationPose>().Single();
        var first = captured.Vertices[0]; pose.Vertices[0] = new(100, 200, 300);
        if (captured.Vertices[0] != first) throw new InvalidOperationException("A queued animation frame shares mutable vertices with the UI.");
    }

    private static void CheckManualSelection()
    {
        using var preview = new M2PreviewView();
        preview.SetDecodedTexture(new RgbaTexture(1, 1, [220, 30, 20, 255]));
        preview.SetGeometry(Model); preview.ClearGeometry(); preview.SetGeometry(Model);
        var canvas = (Control)preview.GetType().GetField("_canvas", Fields).GetValue(preview);
        using var operation = Capture(canvas);
        if (Bitmaps(operation).Length != 1) throw new InvalidOperationException("Replacing comparison geometry lost the user's selected manual texture.");
    }

    private static ICustomDrawOperation Capture(Control canvas)
    {
        var capture = canvas.GetType().GetMethod("CaptureFrame", Fields);
        if (capture is not null) return (ICustomDrawOperation)capture.Invoke(canvas, null);
        var type = canvas.GetType().GetNestedTypes(BindingFlags.NonPublic).Single(type => typeof(ICustomDrawOperation).IsAssignableFrom(type));
        var constructor = type.GetConstructors(Fields).Single();
        var arguments = constructor.GetParameters().Select(parameter =>
        {
            var name = parameter.Name;
            if (name.StartsWith("source")) name = char.ToLowerInvariant(name[6]) + name.Substring(7);
            if (name == "bounds") return (object)canvas.Bounds;
            if (name == "selectedGroup") name = "group";
            return canvas.GetType().GetField("_" + name, Fields).GetValue(canvas);
        }).ToArray();
        return (ICustomDrawOperation)constructor.Invoke(arguments);
    }

    private static void CheckInteraction()
    {
        using var preview = new M2PreviewView();
        var model = Model with { Sequences = [new(0, 0, 0, 1000, 0, 0, 0, 0, 0, 0, -1, 0), new(1, 5, 0, 1200, 0, 0, 0, 0, 0, 0, -1, 0)] };
        var rigType = typeof(M2PreviewGeometry).Assembly.GetType("WoWCrucible.Core.M2AnimationRig", true);
        var rigConstructor = rigType.GetConstructors(Fields).Single();
        var rig = rigConstructor.Invoke(rigConstructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "sequences" => (object)model.Sequences,
            "previewCameras" => Array.Empty<M2PreviewCamera>(), "previewLights" => Array.Empty<M2PreviewLight>(),
            _ => Array.CreateInstance(parameter.ParameterType.GetElementType(), parameter.Name == "vertexSkin" ? model.Vertices.Count : 0)
        }).ToArray());
        typeof(M2PreviewGeometry).GetProperty("AnimationRig", Fields).SetValue(model, rig);
        preview.SetGeometry(model);
        var canvas = (Control)Get(preview, "_canvas");
        var camera = (ModelPreviewCamera)Get(canvas, "_camera");
        camera.PanTarget(30, -45, 800, 600, Matrix4x4.Identity); camera.Orbit(100, 15); camera.Zoom(8); camera.MoveModel(7, 12, 800, 600, Matrix4x4.Identity);
        var expected = camera.State;
        Invoke(preview, "TogglePlayback");
        ((ComboBox)Get(preview, "_sequences")).SelectedIndex = 1;
        if (!(bool)Get(preview, "_playRequested") || !((Avalonia.Threading.DispatcherTimer)Get(preview, "_timer")).IsEnabled)
            throw new InvalidOperationException("Changing animation stopped playback.");
        Invoke(preview, "Scrub", 275d);
        preview.SetGeometry(model with { TriangleIndices = [], Batches = [] });
        if (camera.State != expected || ((ComboBox)Get(preview, "_sequences")).SelectedIndex != 1 || !(bool)Get(preview, "_playRequested"))
            throw new InvalidOperationException("Geoset replacement reset the camera, selected animation or play state.");
        var pose = preview.SnapshotPose();
        if (pose is null || pose.TimeMilliseconds < 275 || pose.TimeMilliseconds > 550)
            throw new InvalidOperationException("Geoset replacement reset the animation position.");
        Invoke(preview, "TogglePlayback"); Invoke(preview, "Scrub", 333d);
        if (((System.Diagnostics.Stopwatch)Get(preview, "_clock")).IsRunning)
            throw new InvalidOperationException("Scrubbing while paused started the clock.");
        preview.SetGeometry(model); if (camera.State != expected) throw new InvalidOperationException("Showing geometry reset the camera.");
        Console.WriteLine("PASS geoset changes retain camera target, model offset, rotation, zoom, animation, timeline and playback; animation switching stays playing; paused seek stays paused.");
    }

    private static void CheckDepth()
    {
        Vector3[] vertices = [new(-1, -1, -1), new(1, 1, -1), new(0, 0, 1), new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)];
        var model = new M2PreviewGeometry("depth.m2", "depth.skin", vertices, Enumerable.Repeat(Vector3.UnitX, 6).ToArray(), Uvs.Concat(Uvs).ToArray(), [0, 1, 2, 3, 4, 5], new(-1), new(1), [new(0, 0, 0, "red.blp"), new(1, 0, 0, "green.blp")])
        { Batches = [new(0, 0, 0, 3, 0, 0) { RenderFlags = 5 }, new(1, 0, 3, 3, 1, 1) { RenderFlags = 5 }] };
        using var preview = new M2PreviewView();
        var canvas = (Control)Get(preview, "_canvas"); canvas.Measure(new Size(256, 256)); canvas.Arrange(new Rect(0, 0, 256, 256));
        preview.SetGeometry(model); var camera = (ModelPreviewCamera)Get(canvas, "_camera"); camera.Orbit(0, -camera.State.Pitch / 0.012f);
        preview.SetDecodedTextures(new Dictionary<int, RgbaTexture> { [0] = new(1, 1, [255, 0, 0, 0]), [1] = new(1, 1, [0, 255, 0, 255]) });
        using (var frame = Capture(canvas)) using (var image = SKBitmap.Decode(Render(frame)))
        {
            var left = image.GetPixel(100, 160); var right = image.GetPixel(156, 160);
            if (left.Green < 240 || left.Red > 10 || right.Red < 240 || right.Green > 10)
                throw new InvalidOperationException($"Intersecting opaque geometry is not depth-tested, or opaque texture alpha made holes: left={left}, right={right}.");
        }
        preview.SetGeometry(model with { Batches = [model.Batches[1], model.Batches[0] with { BlendMode = 1 }] });
        preview.SetDecodedTextures(new Dictionary<int, RgbaTexture> { [0] = new(1, 1, [255, 0, 0, 0]), [1] = new(1, 1, [0, 255, 0, 255]) });
        using (var frame = Capture(canvas)) using (var image = SKBitmap.Decode(Render(frame)))
            if (image.GetPixel(156, 160).Green < 240) throw new InvalidOperationException("Alpha-cutout material did not expose the surface behind it.");
        Console.WriteLine("PASS pixel tests: intersecting surfaces obey per-pixel depth, opaque textures ignore alpha, alpha-cutout holes reveal the rear mesh.");
    }

    private static object Get(object owner, string field) => owner.GetType().GetField(field, Fields).GetValue(owner);
    private static void Invoke(object owner, string method, params object[] args) => owner.GetType().GetMethod(method, Fields).Invoke(owner, args);

    private static void CheckSavedDefaults()
    {
        var assembly = typeof(M2PreviewView).Assembly;
        var settingsType = assembly.GetType("WoWCrucible.Desktop.DesktopSettings", true);
        const string json = "{\"ModelBrowserReviews\":{\"fixture.m2|\":\"Mark for deletion\"},\"ModelBrowserPresets\":{\"fixture.m2|\":{\"GeosetIds\":[0,3201,3203,703],\"Textures\":{\"0\":\"OtherFolder/alternate.blp\",\"2\":\"C:/Textures/pick.blp\"}}}}";
        var settings = System.Text.Json.JsonSerializer.Deserialize(json, settingsType);
        settings = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(settings, settingsType), settingsType);
        var viewType = assembly.GetType("WoWCrucible.Desktop.ModelBrowserView", true);
        using var view = (IDisposable)Activator.CreateInstance(viewType, [settings]);
        var entry = new ModelBrowserEntry("fixture.m2", null, "fixture.m2", 0, "MD20", 264, null);
        ushort[] ids = [0, 3201, 3202, 3203, 703, 3501, 3601];
        var full = Model with
        {
            ModelPath = "fixture.m2", VisibilityMode = M2PreviewVisibilityMode.AllGeosets,
            TriangleIndices = Enumerable.Range(0, ids.Length).SelectMany(_ => new[] { 0, 1, 2 }).ToArray(),
            Submeshes = ids.Select((id, index) => new M2PreviewSubmesh(index, id, 0, 0, 3, index * 3, 3, true)).ToArray(),
            Batches = ids.Select((id, index) => new M2PreviewBatch(index, id, index * 3, 3, index, 0) { RenderFlags = 5 }).ToArray()
        };
        viewType.GetField("_current", Fields).SetValue(view, entry); viewType.GetField("_fullGeometry", Fields).SetValue(view, full);
        Invoke(view, "RestoreGeosets");
        if (!((HashSet<int>)Get(view, "_selectedGeosets")).SetEquals([0, 1, 3, 4]))
            throw new InvalidOperationException("Saved model choices were overridden by automatic defaults.");
        if ((string)viewType.GetMethod("Review", Fields).Invoke(view, [entry]) != "Mark for deletion")
            throw new InvalidOperationException("Deletion mark did not survive settings serialization.");
        var presets = (IDictionary)settingsType.GetProperty("ModelBrowserPresets").GetValue(settings);
        var preset = presets[entry.Identity]; var bindings = (Dictionary<int, string>)preset.GetType().GetProperty("Textures").GetValue(preset);
        if (bindings[0] != "OtherFolder/alternate.blp" || bindings[2] != "C:/Textures/pick.blp")
            throw new InvalidOperationException("Cross-folder texture choices did not survive settings serialization.");
        Console.WriteLine("PASS saved custom face/ear/accessory defaults, cross-folder texture bindings and deletion marks survive JSON round trips; no user settings or source files written.");
    }

    private static SKBitmap[] Bitmaps(object operation) => Objects(operation).OfType<SKBitmap>().ToArray();

    private static IEnumerable<object> Objects(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance); var pending = new Stack<object>(); pending.Push(root);
        while (pending.TryPop(out var value))
        {
            if (value == null || !seen.Add(value)) continue;
            yield return value;
            if (value is SKBitmap or M2PreviewGeometry or WmoPreviewGeometry or M2AnimationPose or string) continue;
            if (value is IEnumerable collection) { foreach (var item in collection) pending.Push(item); continue; }
            var type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) { pending.Push(type.GetProperty("Value").GetValue(value)); continue; }
            if (type.Namespace == "WoWCrucible.Desktop.Controls")
                foreach (var field in type.GetFields(Fields)) pending.Push(field.GetValue(value));
        }
    }

    private static byte[] Render(ICustomDrawOperation operation)
    {
        using var surface = SKSurface.Create(new SKImageInfo(256, 256)); surface.Canvas.Clear(SKColors.Black);
        var type = typeof(ISkiaSharpApiLeaseFeature).Assembly.GetType("Avalonia.Skia.DrawingContextImpl", true);
        var infoType = type.GetNestedType("CreateInfo"); var info = Activator.CreateInstance(infoType);
        infoType.GetField("Canvas").SetValue(info, surface.Canvas); infoType.GetField("Dpi").SetValue(info, new Avalonia.Vector(96, 96));
        using var implementation = (IDisposable)Activator.CreateInstance(type, Fields, null, [info, Array.Empty<IDisposable>()], null);
        using var context = (ImmediateDrawingContext)Activator.CreateInstance(typeof(ImmediateDrawingContext), Fields, null, [implementation, false], null);
        operation.Render(context);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
