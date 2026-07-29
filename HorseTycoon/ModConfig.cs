using StardewModdingAPI;

namespace HorseTycoon
{
	public class ModConfig
	{
		public SButton JumpButton { get; set; } = SButton.Space;

		/// <summary>When true, Shift opens the timing-bar sprint minigame instead of applying the old
		/// flat stat-based sprint buff. Off by default: the minigame is opt-in while it's being tested.
		/// See <see cref="SprintMinigameManager"/>.</summary>
		public bool UseSprintMinigame { get; set; } = false;

		/// <summary>Debug hotkey that flips <see cref="UseSprintMinigame"/> mid-session so the two
		/// sprint systems can be compared back to back.</summary>
		public SButton SprintModeToggleButton { get; set; } = SButton.F7;
	}
}
