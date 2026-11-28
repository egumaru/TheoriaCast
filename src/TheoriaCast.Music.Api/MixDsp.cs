namespace TheoriaCast.Music.Api;

internal static class MixDsp
{
    internal const string MixerId = "mixer";
    internal static MixProcessor Mixer() => new(MixerId, "lane_mixer", []);
    private sealed record Parameter(string Name, double Default, double Min, double Max);
    private static readonly Dictionary<string, Parameter[]> Types = new()
    {
        ["lane_mixer"] = [],
        ["gain_pan"] = [new("gain_db", 0, -120, 36), new("pan", 0, -1, 1)],
        ["hpf"] = [new("frequency_hz", 80, 10, 22000), new("q", 0.7071067811865476, 0.1, 20)],
        ["eq"] = [new("frequency_hz", 1000, 10, 22000), new("gain_db", 0, -24, 24), new("q", 1, 0.1, 20)],
        ["compressor"] = [new("threshold_db", -18, -90, 0), new("ratio", 4, 1, 100),
            new("attack_ms", 10, 0.1, 1000), new("release_ms", 100, 1, 5000)],
        ["limiter"] = [new("ceiling_db", -1, -60, 0)],
        ["reverb"] = [new("decay_s", 1.2, 0.05, 10), new("room_size", 0.5, 0, 1),
            new("wet", 0.2, 0, 1), new("dry", 1, 0, 1), new("predelay_ms", 15, 0, 250)]
    };

    internal static MixProcessor[]? Pin(MixProcessor[]? chain)
    {
        if (chain is not {Length: > 0}) return chain;
        var result = new List<MixProcessor>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in chain)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.id) || !ids.Add(item.id) ||
                item.type is null || !Types.TryGetValue(item.type, out var specs) || item.parameters is null ||
                (item.id == MixerId) != (item.type == "lane_mixer"))
                throw new ArgumentException("Invalid DSP ID, type or parameters. The lane mixer ID is mixer.");
            var values = new Dictionary<string,double>();
            foreach (var spec in specs)
            {
                double value = item.parameters.GetValueOrDefault(spec.Name, spec.Default);
                if (!double.IsFinite(value) || value < spec.Min || value > spec.Max)
                    throw new ArgumentException($"{item.id}.{spec.Name} must be between {spec.Min} and {spec.Max}.");
                values.Add(spec.Name, value);
            }
            if (item.parameters.Keys.Any(key => !values.ContainsKey(key)))
                throw new ArgumentException($"Unknown parameter on {item.id}.");
            result.Add(item with {parameters=values});
        }
        if (!ids.Contains(MixerId)) result.Insert(0, Mixer());
        if (result.Count > 32) throw new ArgumentException("A lane supports up to 32 processors including its mixer.");
        return result.ToArray();
    }
}

public sealed partial class MusicStore
{
    internal const string DspSchema = "theoriacast.dsp-mix/v3";
    private MixManifest EditDsp(MixManifest manifest, MixProjectCommand command)
    {
        var edit = command.dsp ?? throw new ArgumentException("Supply dsp {lane,id}.");
        var tracks = manifest.tracks.ToArray();
        int laneIndex = Array.FindIndex(tracks, lane => lane.id == edit.lane);
        if (laneIndex < 0) throw new KeyNotFoundException("Unknown DSP lane.");
        var lane = tracks[laneIndex];
        var chain = lane.dsp is {Length: > 0} ? lane.dsp.ToList() : [MixDsp.Mixer()];
        int oldIndex = chain.FindIndex(p => p.id == edit.id);
        if (command.action != "add_dsp" && oldIndex < 0) throw new KeyNotFoundException("Unknown processor.");
        switch (command.action)
        {
            case "add_dsp":
                if (oldIndex >= 0 || edit.type is null) throw new ArgumentException("Supply a new ID and type.");
                int at = edit.index ?? chain.Count;
                if (at < 0 || at > chain.Count) throw new ArgumentException("Invalid insertion index.");
                chain.Insert(at, new(edit.id, edit.type, edit.parameters ?? []));
                break;
            case "set_dsp":
                if (edit.parameters is not {Count: > 0}) throw new ArgumentException("Supply parameters to change.");
                if (edit.id == MixDsp.MixerId)
                {
                    if (edit.parameters.Keys.Any(k => k is not ("gain_db" or "pan")))
                        throw new ArgumentException("The lane mixer accepts gain_db and pan.");
                    lane = lane with {gain_db=edit.parameters.GetValueOrDefault("gain_db", lane.gain_db),
                        pan=edit.parameters.GetValueOrDefault("pan", lane.pan)};
                }
                else
                {
                    var values = new Dictionary<string,double>(chain[oldIndex].parameters);
                    foreach (var (key, value) in edit.parameters) values[key] = value;
                    chain[oldIndex] = chain[oldIndex] with {parameters=values};
                }
                break;
            case "move_dsp":
                if (edit.index is not int next || next < 0 || next >= chain.Count)
                    throw new ArgumentException("Supply the zero-based final index.");
                var item = chain[oldIndex];
                chain.RemoveAt(oldIndex);
                chain.Insert(next, item);
                break;
            case "delete_dsp":
                if (edit.id == MixDsp.MixerId) throw new ArgumentException("The lane mixer is permanent. Set its gain and pan to zero for unity.");
                chain.RemoveAt(oldIndex);
                break;
        }
        tracks[laneIndex] = lane with {dsp=chain.ToArray()};
        return Pin(manifest with {schema=DspSchema, tracks=tracks});
    }
}
