using UnityEngine;

namespace BPRE.Multiplayer
{
	/// <summary>
	/// Everybody builds and drives in the same level; no rules, no scores.
	/// The host can make the other players' contraptions physically solid, so vehicles
	/// can push, carry and ram each other.
	/// </summary>
	public sealed class FreePlayMode : MultiplayerGameMode, IGhostCollisionHandler
	{
		private const byte MsgSolid = 10;

		private readonly GhostSolidity m_solidity;

		public FreePlayMode(MultiplayerSession session)
			: base(session)
		{
			m_solidity = new GhostSolidity(this);
			// Off by default: with solid ghosts every player sees a slightly different
			// outcome of a collision, because each vehicle is simulated by its owner only.
			m_solidity.SetEnabled(false, null);
		}

		public override MultiplayerModeId Id => MultiplayerModeId.FreePlay;

		public override string DisplayName => "Free Play";

		public override string Description => SolidContraptions
			? "Build and drive together in the same level. Contraptions are solid: you can push and ram each other."
			: "Build and drive together in the same level. Other players' contraptions are ghosts and pass through each other.";

		public bool SolidContraptions => m_solidity.Enabled;

		public override void OnExit()
		{
			m_solidity.ClearAll();
		}

		public override void Update()
		{
			m_solidity.Update(LocalInLevel);
		}

		public override void OnGhostCreated(RemoteContraption ghost)
		{
			m_solidity.Add(ghost);
		}

		public override void OnGhostDestroyed(RemoteContraption ghost)
		{
			m_solidity.Remove(ghost);
		}

		public override void OnLocalLocationChanged()
		{
			m_solidity.ResetLevel();
		}

		public void OnHitboxCollision(GhostHitbox hitbox, Collision collision)
		{
			// Free Play has no scoring; the collision itself is the whole point.
		}

		public override void OnPlayerJoined(MultiplayerPlayer player)
		{
			if (IsHost)
			{
				bool solid = SolidContraptions;
				SendTo(player, MsgSolid, w => w.Write(solid));
			}
		}

		public override void OnModeMessage(MultiplayerPlayer from, byte subType, NetReader reader)
		{
			if (subType == MsgSolid && from.IsHost)
			{
				ApplySolid(reader.ReadBool(), announce: true);
			}
		}

		/// <summary>Host only: switches collisions on or off for everybody.</summary>
		public void SetSolid(bool solid)
		{
			if (!IsHost || solid == SolidContraptions)
			{
				return;
			}
			ApplySolid(solid, announce: true);
			Send(MsgSolid, w => w.Write(solid));
		}

		private void ApplySolid(bool solid, bool announce)
		{
			if (solid == SolidContraptions)
			{
				return;
			}
			m_solidity.SetEnabled(solid, ContraptionSync.Instance != null ? ContraptionSync.Instance.Ghosts : null);
			if (announce)
			{
				Announce(solid
					? "Contraptions are now solid - you can push and ram each other."
					: "Contraptions pass through each other again.");
			}
		}

		public override void DrawWindow(MultiplayerUI ui)
		{
			GUILayout.Label(Description, ui.Label);
			if (IsHost)
			{
				bool solid = GUILayout.Toggle(SolidContraptions, " Solid contraptions (collisions between players)", ui.ToggleStyle);
				if (solid != SolidContraptions)
				{
					SetSolid(solid);
				}
				GUILayout.Label("Pick any sandbox, race or story level; everybody follows you there.", ui.Label);
			}
			else
			{
				GUILayout.Label(SolidContraptions ? "Collisions: on" : "Collisions: off (host decides)", ui.Label);
			}
			if (SolidContraptions)
			{
				GUILayout.Label("Note: each vehicle is simulated by its own player, so a hard crash can look a little different on each screen.", ui.Label);
			}
		}

		public override void DrawHud(MultiplayerUI ui)
		{
			if (!LocalInLevel || !SolidContraptions)
			{
				return;
			}
			float width = 260f * ui.Scale;
			float lineHeight = 20f * ui.Scale;
			ui.DrawShadowedLabel(new Rect(Screen.width - width - 8f * ui.Scale, 8f * ui.Scale, width, lineHeight), "Free Play  -  solid", ui.Hud);
		}
	}
}
