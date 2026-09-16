using System;
using System.Collections.Generic;
using UnityEngine;

namespace BPRE.Multiplayer
{
	public enum MultiplayerModeId : byte
	{
		FreePlay = 0,
		Distance = 1,
		CaptureTheFlag = 2,
		Battle = 3
	}

	/// <summary>
	/// Base class for the rule sets that run on top of a session. A mode instance
	/// exists on every participant while it is selected; the host is authoritative
	/// for rounds and scores, everybody reports what happens locally.
	/// Sub types 1-9 of mode messages are reserved for <see cref="RoundBasedMode"/>.
	/// </summary>
	public abstract class MultiplayerGameMode
	{
		public static readonly MultiplayerModeId[] AllModes =
		{
			MultiplayerModeId.FreePlay,
			MultiplayerModeId.Distance,
			MultiplayerModeId.CaptureTheFlag,
			MultiplayerModeId.Battle
		};

		protected MultiplayerSession Session { get; }

		public abstract MultiplayerModeId Id { get; }

		public abstract string DisplayName { get; }

		public abstract string Description { get; }

		protected bool IsHost => Session.IsHost;

		protected MultiplayerPlayer LocalPlayer => Session.LocalPlayer;

		protected bool LocalInLevel => LocalPlayer != null && LocalPlayer.InLevel;

		protected static LevelManager LevelManager => WPFMonoBehaviour.levelManager;

		protected MultiplayerGameMode(MultiplayerSession session)
		{
			Session = session;
		}

		public static MultiplayerGameMode Create(MultiplayerModeId id, MultiplayerSession session)
		{
			switch (id)
			{
			case MultiplayerModeId.Distance:
				return new DistanceMode(session);
			case MultiplayerModeId.CaptureTheFlag:
				return new CaptureTheFlagMode(session);
			case MultiplayerModeId.Battle:
				return new BattleMode(session);
			default:
				return new FreePlayMode(session);
			}
		}

		public static string GetDisplayName(MultiplayerModeId id)
		{
			switch (id)
			{
			case MultiplayerModeId.Distance:
				return "Distance";
			case MultiplayerModeId.CaptureTheFlag:
				return "Capture the Flag";
			case MultiplayerModeId.Battle:
				return "Battle";
			default:
				return "Free Play";
			}
		}

		/// <summary>Mode became the active mode of the session.</summary>
		public virtual void OnEnter()
		{
		}

		/// <summary>Mode is being replaced or the session ends. Remove everything you spawned.</summary>
		public virtual void OnExit()
		{
		}

		/// <summary>Host only: the session has just been created with this mode.</summary>
		public virtual void OnSessionStarted()
		{
		}

		public virtual void Update()
		{
		}

		public virtual void OnPlayerJoined(MultiplayerPlayer player)
		{
		}

		public virtual void OnPlayerLeft(MultiplayerPlayer player)
		{
		}

		public virtual void OnPlayerLocationChanged(MultiplayerPlayer player)
		{
		}

		public virtual void OnLocalLocationChanged()
		{
		}

		/// <summary>The local player pressed play; the contraption is simulating and being replicated.</summary>
		public virtual void OnLocalContraptionStarted(Contraption contraption, IReadOnlyList<BasePart> parts)
		{
		}

		public virtual void OnLocalContraptionStopped()
		{
		}

		public virtual void OnGhostCreated(RemoteContraption ghost)
		{
		}

		public virtual void OnGhostDestroyed(RemoteContraption ghost)
		{
		}

		public virtual void OnModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
		}

		/// <summary>Draws the mode section inside the multiplayer window.</summary>
		public virtual void DrawWindow(MultiplayerUI ui)
		{
			GUILayout.Label(Description, ui.Label);
		}

		/// <summary>Draws in-game overlay elements (timers, scores, objectives).</summary>
		public virtual void DrawHud(MultiplayerUI ui)
		{
		}

		protected void Send(byte subType, Action<NetWriter> payload = null)
		{
			Session.SendModeMessage(subType, payload);
		}

		protected void SendTo(MultiplayerPlayer player, byte subType, Action<NetWriter> payload = null)
		{
			Session.SendModeMessageTo(player, subType, payload);
		}

		protected void Announce(string text)
		{
			Session.AddSystemChat(text);
		}
	}
}
