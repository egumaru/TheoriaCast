using System.Text.Json;

namespace TheoriaCast.Music;

public static class PrivateJson
{
    public static void Write<T>(string path, T value, bool overwrite = true)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode=FileMode.CreateNew, Access=FileAccess.Write, Share=FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var file = new FileStream(temp,options))
                JsonSerializer.Serialize(file,value,new JsonSerializerOptions { WriteIndented=true });
            File.Move(temp,path,overwrite);
        }
        finally { File.Delete(temp); }
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("設定ファイルが空です。");
}
