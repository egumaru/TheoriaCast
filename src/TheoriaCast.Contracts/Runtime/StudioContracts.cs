using System;

// Kept within Unity's C# / JsonUtility feature set and compiled into the .NET host too.
namespace TheoriaCast.Contracts
{
    [Serializable] public sealed class Vec3
    {
        public float x, y, z;
        public Vec3() { }
        public Vec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    [Serializable] public sealed class NamedValue { public string name = ""; public float value; }
    [Serializable] public sealed class BonePose { public string name = ""; public Vec3 rotation = new Vec3(); }
    [Serializable] public sealed class InputBinding
    {
        public string channel = "mouth";
        public string source = "manual";
        public string device = "";
    }
    [Serializable] public sealed class RigState
    {
        public float mouth, gazeX, gazeY;
        public Vec3 headRotation = new Vec3();
        public NamedValue[] expressions = Array.Empty<NamedValue>();
        public NamedValue[] parameters = Array.Empty<NamedValue>();
        public BonePose[] bones = Array.Empty<BonePose>();
    }
    [Serializable] public sealed class ModelState
    {
        public string id = "", name = "", path = "", format = "";
        public bool visible = true;
        public Vec3 position = new Vec3(), rotation = new Vec3();
        public float scale = 1;
        public RigState rig = new RigState();
        public InputBinding[] inputs = Array.Empty<InputBinding>();
    }
    [Serializable] public sealed class OutputSettings
    {
        public bool inputsEnabled;
        public int width = 1280, height = 720, fps = 60;
        public string background = "#18202E";
        public Vec3 cameraPosition = new Vec3(0, 1, -3);
        public Vec3 cameraTarget = new Vec3(0, 1, 0);
        public float fieldOfView = 35;
    }
    [Serializable] public sealed class SceneState
    {
        public long revision;
        public ModelState[] models = Array.Empty<ModelState>();
        public OutputSettings output = new OutputSettings();
    }
    [Serializable] public sealed class ActionStep
    {
        public string operation = "wait", modelId = "", name = "", text = "", channel = "";
        public float value;
        public Vec3 vector = new Vec3();
        public bool enabled = true;
        public int durationMs;
    }
    [Serializable] public sealed class ActionDefinition
    {
        public string id = "", label = "", kind = "script";
        public SceneState preset = new SceneState();
        public ActionStep[] steps = Array.Empty<ActionStep>();
        public int repeat = 1;
    }
    [Serializable] public sealed class PlaybackState
    {
        public string actionId = "", label = "", status = "idle", error = "";
        public int stepIndex, stepCount, iteration;
    }
    [Serializable] public sealed class ModelReport
    {
        public string modelId = "", status = "loading", error = "";
        public string[] expressions = Array.Empty<string>(), parameters = Array.Empty<string>(), bones = Array.Empty<string>();
    }
    [Serializable] public sealed class RendererReport
    {
        public string id = "", role = "", version = "";
        public long appliedRevision;
        public ModelReport[] models = Array.Empty<ModelReport>();
        public string[] microphones = Array.Empty<string>(), cameras = Array.Empty<string>();
    }
    [Serializable] public sealed class StudioSnapshot
    {
        public SceneState scene = new SceneState();
        public ActionDefinition[] actions = Array.Empty<ActionDefinition>();
        public PlaybackState playback = new PlaybackState();
        public RendererReport[] renderers = Array.Empty<RendererReport>();
    }
}
