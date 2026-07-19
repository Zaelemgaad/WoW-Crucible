namespace WoWCrucible.Core;

public enum M2TextureRenderPassBlend { Source, Modulate, Add, AddNoAlpha, DestinationOut }
public sealed record M2TextureRenderPass(int StageIndex, M2TextureRenderPassBlend Blend, bool UseLighting);

/// <summary>Produces a deterministic pass plan for supported build-264 texture combiners.</summary>
public static class M2TextureCombinerRenderPlanService
{
    public static IReadOnlyList<M2TextureRenderPass> Build(M2PreviewTextureCombiner combiner, IReadOnlyList<M2PreviewTextureStage> stages)
    {
        ArgumentNullException.ThrowIfNull(combiner); ArgumentNullException.ThrowIfNull(stages);
        if (!combiner.Supported) throw new NotSupportedException($"{combiner.Name} has no validated render plan.");
        if (stages.Count == 0) return [];
        if (combiner.Kind == M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlpha)
        {
            RequireTwo(stages, combiner);
            // lit * mix(base * environment * 2, base, base alpha)
            return [new(0, M2TextureRenderPassBlend.Source, true), new(1, M2TextureRenderPassBlend.Modulate, false), new(0, M2TextureRenderPassBlend.DestinationOut, false), new(0, M2TextureRenderPassBlend.Add, true)];
        }
        if (combiner.Kind == M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlphaAdd)
        {
            if (stages.Count != 3) throw new InvalidDataException($"{combiner.Name} requires exactly three texture stages, but {stages.Count:N0} were supplied.");
            // lit * mix(base * environment * 2, base, base alpha) + additive.rgb * additive.a
            return [new(0, M2TextureRenderPassBlend.Source, true), new(1, M2TextureRenderPassBlend.Modulate, false), new(0, M2TextureRenderPassBlend.DestinationOut, false), new(0, M2TextureRenderPassBlend.Add, true), new(2, M2TextureRenderPassBlend.Add, false)];
        }
        if (combiner.Kind is M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha or M2PreviewTextureCombinerKind.ExplicitModAddAlpha)
        {
            RequireTwo(stages, combiner);
            return [new(0, M2TextureRenderPassBlend.Source, true), new(1, M2TextureRenderPassBlend.Add, false)];
        }
        var result = new M2TextureRenderPass[stages.Count];
        result[0] = new(0, M2TextureRenderPassBlend.Source, true);
        for (var index = 1; index < stages.Count; index++)
            result[index] = new(index, stages[index].Blend switch
            {
                M2PreviewTextureStageBlend.Modulate or M2PreviewTextureStageBlend.Modulate2X => M2TextureRenderPassBlend.Modulate,
                M2PreviewTextureStageBlend.Add => M2TextureRenderPassBlend.Add,
                M2PreviewTextureStageBlend.AddNoAlpha => M2TextureRenderPassBlend.AddNoAlpha,
                _ => throw new NotSupportedException($"{combiner.Name} stage {index:N0} has unsupported blend {stages[index].Blend}.")
            }, false);
        return result;
    }

    private static void RequireTwo(IReadOnlyList<M2PreviewTextureStage> stages, M2PreviewTextureCombiner combiner)
    {
        if (stages.Count != 2) throw new InvalidDataException($"{combiner.Name} requires exactly two texture stages, but {stages.Count:N0} were supplied.");
    }
}
