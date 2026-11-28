namespace TheoriaCast.Music;

public sealed record RecordingImport(string title,string[] chunks);
public sealed record AudioChunk(byte[] wav);
public sealed record StagedChunk(string sha256);
public sealed record ImportedTrack(string track, string sha256, bool created);
