using System.Numerics;
using Microsoft.Extensions.Logging;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Particles;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;

namespace TBGemViewer;

/// <summary>
/// Terrorblade with Fractal Horns of Inner Abysm: the hero model, its default items and the effects they create,
/// tinted by a prismatic gem.
/// </summary>
/// <remarks>
/// The particles of the arcana and of the default items tint themselves with C_INIT_RemapCPtoVector:
/// control point 15 holds the gem color as 0..255 RGB and the x of control point 16 is the strength,
/// 0 for the original colors.
/// </remarks>
sealed class GemScene
{
    public const string ArcanaModel = "models/heroes/terrorblade/terrorblade_arcana.vmdl";

    private const string ArcanaParticles = "particles/econ/items/terrorblade/terrorblade_horns_arcana/";
    private const string HeroParticles = "particles/units/heroes/hero_terrorblade/";
    private const string KillParticle = ArcanaParticles + "terrorblade_arcana_enemy_death.vpcf";
    private const string VictimModel = "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee.vmdl";

    public static readonly Vector3 HeroFocus = new(0, 0, 110);

    private static readonly string[] Wearables =
    [
        "models/heroes/terrorblade/horns_arcana.vmdl",
        "models/heroes/terrorblade/armor.vmdl",
        "models/heroes/terrorblade/weapon.vmdl",
        "models/heroes/terrorblade/wings.vmdl",
    ];

    /// <summary>
    /// What the hero shows with the arcana equipped: the particle_create visuals of the arcana and of
    /// the default weapons and ambient effects in items_game.txt.
    /// </summary>
    /// <remarks>
    /// The default weapons also create terrorblade_ambient_sword_blade(_2), glow planes drawn with
    /// C_OP_RenderModels. The renderer places that model off the blades, as a large ghost of the weapon,
    /// so they are left out; the sword fire still carries the gem color on the blades.
    /// </remarks>
    private static readonly string[] Effects =
    [
        ArcanaParticles + "terrorblade_ambient_body_arcana_horns.vpcf",
        ArcanaParticles + "terrorblade_ambient_eyes_arcana_horns.vpcf",
        HeroParticles + "terrorblade_ambient_sword_l.vpcf",
        HeroParticles + "terrorblade_ambient_sword_r.vpcf",
        HeroParticles + "terrorblade_feet_effects.vpcf",
    ];

    /// <summary>A particle system on the hero, and the attachment each of its control points follows.</summary>
    private sealed record HeroEffect(ParticleSceneNode Node, (int ControlPoint, string Attachment)[] Drivers);

    private readonly Scene scene;
    private readonly ILogger logger;
    private readonly ModelSceneNode hero;
    private readonly List<HeroEffect> heroEffects = [];

    private string loopActivity = "ACT_DOTA_LOADOUT";
    private float oneShotTimeLeft;
    private float heroYaw = -0.3f;

    private ModelSceneNode? victimNode;
    private ParticleSceneNode? killParticle;
    private float killTime;

    public PrismaticGem? Gem { get; private set; }

    public GemScene(Scene scene, ILogger logger)
    {
        this.scene = scene;
        this.logger = logger;

        var model = LoadModel(ArcanaModel) ?? throw new FileNotFoundException("The Terrorblade arcana model is missing from the Dota 2 files", ArcanaModel);
        hero = new ModelSceneNode(scene, model) { Name = ArcanaModel };
        scene.Add(hero, true);

        foreach (var path in Wearables)
        {
            if (LoadModel(path) is not { } wearableModel)
            {
                logger.LogWarning("Missing wearable {Path}", path);
                continue;
            }

            var node = new ModelSceneNode(scene, wearableModel) { Name = path };
            node.SetParent(hero);
            node.BoneMerge(hero);
            scene.Add(node, true);
        }

        foreach (var path in Effects)
        {
            if (CreateHeroEffect(path) is { } effect)
            {
                heroEffects.Add(effect);
            }
        }

        hero.Transform = Matrix4x4.CreateRotationZ(heroYaw);
        PlayActivity("ACT_DOTA_LOADOUT", loop: true);
        ApplyGem();
    }

    private Model? LoadModel(string path)
        => scene.RendererContext.FileLoader.LoadFileCompiled(path)?.DataBlock as Model;

    private ParticleSceneNode? CreateParticle(string path, ModelSceneNode owner)
    {
        if (scene.RendererContext.FileLoader.LoadFileCompiled(path)?.DataBlock is not ParticleSystem system)
        {
            logger.LogWarning("Missing particle {Path}", path);
            return null;
        }

        var node = new ParticleSceneNode(scene, system) { Name = path };
        node.GetControlPoint(0).ModelObject = new ParticleModelNodeObject(owner.Model, owner, scene.RendererContext.FileLoader);
        scene.Add(node, true);
        return node;
    }

    /// <summary>
    /// Places an effect on the hero by the drivers of its first control point configuration, as the game
    /// does for an item effect: point attachments follow that attachment, the rest follow the hero.
    /// </summary>
    private HeroEffect? CreateHeroEffect(string path)
    {
        if (CreateParticle(path, hero) is not { } node
            || scene.RendererContext.FileLoader.LoadFileCompiled(path)?.DataBlock is not ParticleSystem system)
        {
            return null;
        }

        var configurations = system.GetUpgradedData().GetArray("m_controlPointConfigurations");
        var drivers = configurations is { Count: > 0 }
            ? (configurations[0].GetArray("m_drivers") ?? [])
                .Select(static driver => (
                    driver.GetInt32Property("m_iControlPoint"),
                    driver.GetStringProperty("m_attachmentName") ?? string.Empty))
                .Where(driver => driver.Item2.Length == 0 || hero.Attachments.ContainsKey(driver.Item2))
                .ToArray()
            : [];

        var rootAttachment = drivers.FirstOrDefault(static d => d.Item1 == 0).Item2 ?? string.Empty;
        hero.AttachNode(node, rootAttachment);

        return new HeroEffect(node, [.. drivers.Where(static d => d.Item1 != 0)]);
    }

    /// <summary>Finds the arcana version of an activity, which Dota picks with the "abysm" activity modifier.</summary>
    private SequenceAnimation? FindSequence(string activity)
    {
        var candidates = hero.Animations.Values.OfType<SequenceAnimation>()
            .Where(s => !s.Hidden && s.Activities.Any(a => a.Name.Equals(activity, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return candidates.FirstOrDefault(static s => s.Activities.Any(static a => a.Name.Equals("abysm", StringComparison.OrdinalIgnoreCase)))
            ?? candidates.FirstOrDefault(static s => s.Activities.Length == 1)
            ?? candidates.FirstOrDefault();
    }

    public void PlayActivity(string activity, bool loop)
    {
        if (FindSequence(activity) is not { } sequence)
        {
            logger.LogWarning("No sequence for {Activity}", activity);
            return;
        }

        hero.SetAnimation(sequence, blendTime: 0.15f);

        if (loop)
        {
            loopActivity = activity;
            oneShotTimeLeft = 0;
        }
        else
        {
            oneShotTimeLeft = sequence.Duration;
        }
    }

    public void SetGem(PrismaticGem? gem)
    {
        Gem = gem;
        ApplyGem();
    }

    private void ApplyGem()
    {
        var tint = Gem is { } gem ? new Vector3(gem.R, gem.G, gem.B) : Vector3.Zero;
        var strength = Gem != null ? 1f : 0f;

        foreach (var effect in heroEffects)
        {
            ApplyGem(effect.Node, tint, strength);
        }

        if (killParticle != null)
        {
            ApplyGem(killParticle, tint, strength);
        }
    }

    private static void ApplyGem(ParticleSceneNode particle, Vector3 tint, float strength)
    {
        particle.SetControlPoint(15, Matrix4x4.CreateTranslation(tint));
        particle.SetControlPoint(16, Matrix4x4.CreateTranslation(strength, 0, 0));
        particle.Restart();
    }

    /// <summary>Turns the hero around its feet, as a sideways drag does on the hero page.</summary>
    public void Turn(float radians)
    {
        heroYaw += radians;
        hero.Transform = Matrix4x4.CreateRotationZ(heroYaw);
    }

    /// <summary>A creep stands in front of the hero and dies to its attack with the arcana kill effect.</summary>
    public void SpawnKillDemo()
    {
        if (LoadModel(VictimModel) is not { } victimModel)
        {
            return;
        }

        RemoveKillDemo();

        victimNode = new ModelSceneNode(scene, victimModel) { Name = VictimModel };
        victimNode.Transform = Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(140, 0, 0) * hero.Transform;
        scene.Add(victimNode, true);

        killParticle = CreateParticle(KillParticle, victimNode);
        if (killParticle != null)
        {
            killParticle.Transform = victimNode.Transform;
            killParticle.SetControlPoint(1, hero.GetAttachmentTransform("attach_hitloc"));
        }

        killTime = 0;
        ApplyGem();
        PlayActivity("ACT_DOTA_ATTACK", loop: false);
    }

    private void RemoveKillDemo()
    {
        if (killParticle != null)
        {
            scene.Remove(killParticle, true);
            killParticle.Delete();
            killParticle = null;
        }

        if (victimNode != null)
        {
            scene.Remove(victimNode, true);
            victimNode.Delete();
            victimNode = null;
        }
    }

    /// <summary>Moves the effects with their attachments and plays one shot animations back into the loop.</summary>
    public void Update(float frameTime)
    {
        foreach (var effect in heroEffects)
        {
            foreach (var (controlPoint, attachment) in effect.Drivers)
            {
                effect.Node.SetControlPoint(controlPoint, attachment.Length > 0
                    ? hero.GetAttachmentTransform(attachment)
                    : hero.Transform);
            }
        }

        if (oneShotTimeLeft > 0)
        {
            oneShotTimeLeft -= frameTime;

            if (oneShotTimeLeft <= 0)
            {
                PlayActivity(loopActivity, loop: true);
            }
        }

        if (victimNode != null)
        {
            killTime += frameTime;

            // The creep dies as the attack lands, the effect then ends with its endcap
            if (killTime > 0.35f && victimNode.LayerEnabled)
            {
                victimNode.LayerEnabled = false;
            }

            if (killTime > 1.6f && killTime - frameTime <= 1.6f)
            {
                killParticle?.PlayEndCap();
            }

            if (killTime > 4f)
            {
                RemoveKillDemo();
            }
        }
    }
}
