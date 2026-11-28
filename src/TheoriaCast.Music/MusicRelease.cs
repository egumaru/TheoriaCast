using System.Reflection;

namespace TheoriaCast.Music;

public sealed record MusicRelease(string Product, string Version, string Build)
{
    public static MusicRelease Of(Assembly assembly)
    {
        var name = assembly.GetName();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? name.Version!.ToString(3);
        return new(name.Name!, version, assembly.ManifestModule.ModuleVersionId.ToString("N"));
    }
}
