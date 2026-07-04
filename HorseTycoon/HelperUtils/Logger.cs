using StardewModdingAPI;

namespace HorseTycoon
{
    /// <summary>Global logging helper. Call <see cref="Init"/> once from the mod entry.</summary>
    internal static class Logger
    {
        private static IMonitor? Monitor;

        /// <summary>Toggle for the mod's own verbose logging (independent of SMAPI's verbose setting).</summary>
        public static bool VerboseLogging = true;

        public static void Init(IMonitor monitor) => Monitor = monitor;

        /// <summary>Logs at Debug level, gated on <see cref="VerboseLogging"/>.</summary>
        public static void LogVerbose(string message)
        {
            if (VerboseLogging)
                Monitor?.Log(message, LogLevel.Debug);
        }
    }
}
