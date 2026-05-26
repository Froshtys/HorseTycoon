using StardewModdingAPI;

namespace HorseTycoon
{
    public interface IContentPatcherApi
    {
        // Must return IEnumerable<string> to be compatible
        void RegisterToken(IManifest mod, string name, Func<IEnumerable<string>> getValue);
    }
}