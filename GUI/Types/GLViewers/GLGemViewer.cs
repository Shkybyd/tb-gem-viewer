using System.Linq;
using GUI.Utils;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Particles;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// Terrorblade arcana (Fractal Horns of Inner Abysm) with prismatic gem preview, laid out like the Dota hero loadout.
    /// </summary>
    /// <remarks>
    /// The particles of the arcana and of the default items tint themselves with C_INIT_RemapCPtoVector:
    /// control point 15 holds the gem color as 0..255 RGB and the x of control point 16 is the strength,
    /// 0 for the original colors.
    /// </remarks>
    sealed partial class GLGemViewer : GLModelViewer
    {
        public const string CommandLineSwitch = "--tbgem";
        public const string WindowTitle = "TB Gem Viewer - Fractal Horns of Inner Abysm";
        public const string ArcanaModel = "models/heroes/terrorblade/terrorblade_arcana.vmdl";
        private const string ArcanaLink = "vpk:steam:570/game/dota/pak01_dir.vpk:" + ArcanaModel + "_c";

        /// <summary>Whether the arcana model opens in this viewer, set by <see cref="CommandLineSwitch"/>.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>
        /// Takes <see cref="CommandLineSwitch"/> out of the arguments and opens the arcana model instead.
        /// Started without arguments, the app is the showcase.
        /// </summary>
        public static void TakeSwitch(ref string[] args)
        {
            if (args.Length > 0 && !args.Contains(CommandLineSwitch, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Enabled = true;
            args = [.. args.Where(static a => !a.Equals(CommandLineSwitch, StringComparison.OrdinalIgnoreCase)), ArcanaLink];
        }

        public static bool IsArcanaModel(Model model)
            => Enabled && model.Name.Equals(ArcanaModel, StringComparison.OrdinalIgnoreCase);

        private const string ArcanaParticles = "particles/econ/items/terrorblade/terrorblade_horns_arcana/";
        private const string HeroParticles = "particles/units/heroes/hero_terrorblade/";
        private const string KillParticle = ArcanaParticles + "terrorblade_arcana_enemy_death.vpcf";
        private const string VictimModel = "models/creeps/lane_creeps/creep_bad_melee/creep_bad_melee.vmdl";

        private static readonly Vector3 HeroFocus = new(0, 0, 110);

        // A narrow lens like the loadout camera, which keeps the hero from bending at the edges
        private const float ShowcaseFieldOfView = 40;

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
        /// C_OP_RenderModels. The viewer places that model off the blades, as a large ghost of the weapon,
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

        private readonly List<ModelSceneNode> wearableNodes = [];
        private readonly List<HeroEffect> heroEffects = [];
        private PrismaticGem? currentGem;
        private PrismaticGem? previousGem;
        private string? loopActivity = "ACT_DOTA_LOADOUT";
        private float oneShotTimeLeft;

        private ModelSceneNode? victimNode;
        private ParticleSceneNode? killParticle;
        private float killTime;

        public GLGemViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, Model model)
            : base(vrfGuiContext, rendererContext, model)
        {
        }

        protected override void LoadScene()
        {
            base.LoadScene();
            var hero = modelSceneNode ?? throw new InvalidOperationException("Arcana model failed to load");

            foreach (var path in Wearables)
            {
                if (LoadModel(path) is not { } wearableModel)
                {
                    Log.Warn(nameof(GLGemViewer), $"Missing wearable {path}");
                    continue;
                }

                var node = new ModelSceneNode(Scene, wearableModel) { Name = path };
                node.SetParent(hero);
                node.BoneMerge(hero);
                Scene.Add(node, true);
                wearableNodes.Add(node);
            }

            foreach (var path in Effects)
            {
                if (CreateHeroEffect(path, hero) is { } effect)
                {
                    heroEffects.Add(effect);
                }
            }

            // As in the game, the camera stays put and a drag turns the hero around
            FixedCamera = true;
            FieldOfViewOverride = ShowcaseFieldOfView;
            SetFieldOfView(ShowcaseFieldOfView);
            heroYaw = -0.3f;
            hero.Transform = Matrix4x4.CreateRotationZ(heroYaw);

            PlayActivity("ACT_DOTA_LOADOUT", loop: true, makeCurrent: false);
            ApplyGem();
            ApplyView();
        }

        private Model? LoadModel(string path)
            => GuiContext.LoadFileCompiled(path)?.DataBlock as Model;

        private ParticleSceneNode? CreateParticle(string path, ModelSceneNode owner)
        {
            if (GuiContext.LoadFileCompiled(path)?.DataBlock is not ParticleSystem system)
            {
                Log.Warn(nameof(GLGemViewer), $"Missing particle {path}");
                return null;
            }

            var node = new ParticleSceneNode(Scene, system) { Name = path };
            node.GetControlPoint(0).ModelObject = new ParticleModelNodeObject(owner.Model, owner, Scene.RendererContext.FileLoader);
            Scene.Add(node, true);
            return node;
        }

        /// <summary>
        /// Places an effect on the hero by the drivers of its first control point configuration, as the game
        /// does for an item effect: point attachments follow that attachment, the rest follow the hero.
        /// </summary>
        private HeroEffect? CreateHeroEffect(string path, ModelSceneNode hero)
        {
            if (CreateParticle(path, hero) is not { } node
                || GuiContext.LoadFileCompiled(path)?.DataBlock is not ParticleSystem system)
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
            var candidates = modelSceneNode!.Animations.Values.OfType<SequenceAnimation>()
                .Where(s => !s.Hidden && s.Activities.Any(a => a.Name.Equals(activity, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return candidates.FirstOrDefault(static s => s.Activities.Any(static a => a.Name.Equals("abysm", StringComparison.OrdinalIgnoreCase)))
                ?? candidates.FirstOrDefault(static s => s.Activities.Length == 1)
                ?? candidates.FirstOrDefault();
        }

        private void PlayActivity(string activity, bool loop, bool makeCurrent = true)
        {
            if (modelSceneNode == null || FindSequence(activity) is not { } sequence)
            {
                Log.Warn(nameof(GLGemViewer), $"No sequence for {activity}");
                return;
            }

            if (makeCurrent)
            {
                using var lockedGL = MakeCurrent();
                modelSceneNode.SetAnimation(sequence, blendTime: 0.15f);
            }
            else
            {
                modelSceneNode.SetAnimation(sequence);
            }

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

        private void ApplyGem()
        {
            var tint = currentGem is { } gem ? new Vector3(gem.Color.R, gem.Color.G, gem.Color.B) : Vector3.Zero;
            var strength = currentGem != null ? 1f : 0f;

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

        private void SelectGem(PrismaticGem? gem)
        {
            if (gem == currentGem)
            {
                return;
            }

            previousGem = currentGem;
            currentGem = gem;

            using (MakeCurrent())
            {
                ApplyGem();
            }

            OnGemChanged();
        }

        // Both views stand on the stage of the hero page, which the panels over it are painted to match
        private void ApplyView()
        {
            Renderer.BaseBackground?.SetLobbyBackground(true);
            ShowBaseGrid = false;
        }

        // The hero page of the Dota menu: a front view of the loadout pose on the dashboard backdrop
        private void LobbyCamera()
        {
            ApplyView();
            SetLobbyCameraPose();
            PlayActivity("ACT_DOTA_LOADOUT", loop: true);
        }

        // Where the camera looks, the direction it looks from and how far away, with the closest and farthest zoom
        private Vector3 cameraTarget;
        private Vector3 cameraDirection;
        private float cameraDistance;
        private float minCameraDistance;
        private float maxCameraDistance;
        private float heroYaw;

        private void SetLobbyCameraPose()
        {
            var offset = new Vector3(640, 0, 105);
            SetCamera(HeroFocus - new Vector3(0, 0, 6), offset, 330, 1000);
        }

        private void SetCamera(Vector3 target, Vector3 offset, float minDistance, float maxDistance)
        {
            cameraTarget = target;
            cameraDirection = Vector3.Normalize(offset);
            cameraDistance = offset.Length();
            minCameraDistance = minDistance;
            maxCameraDistance = maxDistance;
            ApplyCamera();
        }

        private void ApplyCamera()
        {
            Input.Camera.SetLocation(cameraTarget + cameraDirection * cameraDistance);
            Input.Camera.LookAt(cameraTarget);
        }

        /// <summary>A sideways drag turns the hero around, the wheel moves the camera closer or farther.</summary>
        protected override void OnFixedCameraInput(Vector2 mouseDelta, int wheelDelta)
        {
            if (mouseDelta.X != 0 && modelSceneNode != null)
            {
                heroYaw += mouseDelta.X * 0.006f;
                modelSceneNode.Transform = Matrix4x4.CreateRotationZ(heroYaw);
            }

            if (wheelDelta != 0)
            {
                cameraDistance = Math.Clamp(cameraDistance * MathF.Pow(0.9f, wheelDelta / 120f), minCameraDistance, maxCameraDistance);
                ApplyCamera();
            }
        }

        // The Dota game camera looks north and about 60 degrees down from 1134 units away
        private void GameCamera()
        {
            ApplyView();
            var pitch = float.DegreesToRadians(60);
            SetCamera(new Vector3(0, 0, 60), new Vector3(0, -MathF.Cos(pitch), MathF.Sin(pitch)) * 1134, 600, 1600);
            PlayActivity("ACT_DOTA_IDLE", loop: true);
        }

        private void SpawnKillDemo()
        {
            var hero = modelSceneNode!;

            if (LoadModel(VictimModel) is not { } victimModel)
            {
                return;
            }

            using (MakeCurrent())
            {
                RemoveKillDemo();

                victimNode = new ModelSceneNode(Scene, victimModel) { Name = VictimModel };
                victimNode.Transform = Matrix4x4.CreateRotationZ(MathF.PI) * Matrix4x4.CreateTranslation(140, 0, 0) * hero.Transform;
                Scene.Add(victimNode, true);

                killParticle = CreateParticle(KillParticle, victimNode);
                if (killParticle != null)
                {
                    killParticle.Transform = victimNode.Transform;
                    killParticle.SetControlPoint(1, hero.GetAttachmentTransform("attach_hitloc"));
                }

                killTime = 0;
                ApplyGem();
            }

            PlayActivity("ACT_DOTA_ATTACK", loop: false);
        }

        private void RemoveKillDemo()
        {
            if (killParticle != null)
            {
                Scene.Remove(killParticle, true);
                killParticle.Delete();
                killParticle = null;
            }

            if (victimNode != null)
            {
                Scene.Remove(victimNode, true);
                victimNode.Delete();
                victimNode = null;
            }
        }

        private bool initialCameraPending = true;

        protected override void OnUpdate(float frameTime)
        {
            // The viewer frames the model once it loads, the showcase starts from the lobby view instead
            if (initialCameraPending)
            {
                initialCameraPending = false;
                SetLobbyCameraPose();
            }

            if (modelSceneNode != null)
            {
                foreach (var effect in heroEffects)
                {
                    foreach (var (controlPoint, attachment) in effect.Drivers)
                    {
                        effect.Node.SetControlPoint(controlPoint, attachment.Length > 0
                            ? modelSceneNode.GetAttachmentTransform(attachment)
                            : modelSceneNode.Transform);
                    }
                }
            }

            if (oneShotTimeLeft > 0)
            {
                oneShotTimeLeft -= frameTime;

                if (oneShotTimeLeft <= 0 && loopActivity != null)
                {
                    // The render thread already holds the GL context here
                    PlayActivity(loopActivity, loop: true, makeCurrent: false);
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

            base.OnUpdate(frameTime);
        }
    }
}
