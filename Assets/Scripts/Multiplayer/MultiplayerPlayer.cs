namespace BPRE.Multiplayer
{
	/// <summary>One participant of the session, as seen by the local game.</summary>
	public sealed class MultiplayerPlayer
	{
		public int Id;

		public string Name = "?";

		public bool IsHost;

		/// <summary>Scene the player is currently inside, empty when not in a level.</summary>
		public string SceneName = string.Empty;

		/// <summary>Level identifier (sandbox/race id or episode label) to tell same-scene variants apart.</summary>
		public string LevelIdentifier = string.Empty;

		/// <summary>Host side only: the socket this player talks through. Null for the local player.</summary>
		public NetConnection Connection;

		/// <summary>Raw ContraptionStart message of the contraption the player is currently running, if any.</summary>
		public byte[] LastContraptionStart;

		public bool IsLocal;

		public bool InLevel => !string.IsNullOrEmpty(SceneName);

		public bool IsInSameLevel(MultiplayerPlayer other)
		{
			if (other == null || !InLevel || !other.InLevel)
			{
				return false;
			}
			return SceneName == other.SceneName && LevelIdentifier == other.LevelIdentifier;
		}

		public string LocationLabel
		{
			get
			{
				if (!InLevel)
				{
					return "in menu";
				}
				return string.IsNullOrEmpty(LevelIdentifier) ? SceneName : LevelIdentifier;
			}
		}
	}
}
