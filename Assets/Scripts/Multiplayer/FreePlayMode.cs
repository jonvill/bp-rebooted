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

		private const byte MsgPush = 11;

		private const float MinPushSpeed = 1.5f;

		private const float PushShare = 0.6f;

		private const float MaxPushSpeed = 20f;

		private const float PushCooldown = 0.15f;

		private readonly GhostSolidity m_solidity;

		public FreePlayMode(MultiplayerSession session)
			: base(session)
		{
			m_solidity = new GhostSolidity(this);
			// On by default so vehicles interact. Each vehicle is still simulated by its owner,
			// so hits are forwarded to the owner as a push (see MsgPush).
			m_solidity.SetEnabled(true, null);
		}

		public override MultiplayerModeId Id => MultiplayerModeId.FreePlay;

		public override string DisplayName => "Free Play";

		public override string Description => SolidContraptions
			? "Play together. Contraptions can push and ram."
			: "Play together. Contraptions pass through each other.";

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
			// The ghost is kinematic here, so our car bounces off it but the real vehicle on the
			// owner's machine would not move. Tell the owner to take the hit.
			if (!SolidContraptions || hitbox == null || hitbox.Owner == null || hitbox.Owner.Player == null || collision.collider == null)
			{
				return;
			}
			BasePart part = collision.collider.GetComponentInParent<BasePart>();
			if (part == null || part.rigidbody == null || ContraptionSync.Instance == null || part.contraption != ContraptionSync.Instance.TrackedContraption)
			{
				return;
			}
			if (Time.realtimeSinceStartup - hitbox.LastHitTime < PushCooldown)
			{
				return;
			}
			Vector3 velocity = part.rigidbody.velocity;
			velocity.z = 0f;
			if (velocity.magnitude < MinPushSpeed)
			{
				return;
			}
			hitbox.LastHitTime = Time.realtimeSinceStartup;
			Vector3 push = Vector3.ClampMagnitude(velocity * PushShare, MaxPushSpeed);
			int targetId = hitbox.Owner.Player.Id;
			Send(MsgPush, w => w.Write(targetId).Write(push.x).Write(push.y));
		}

		private static void ApplyPush(Vector2 push)
		{
			Contraption contraption = ContraptionSync.Instance != null ? ContraptionSync.Instance.TrackedContraption : null;
			if (contraption == null)
			{
				return;
			}
			Vector3 deltaV = new Vector3(Mathf.Clamp(push.x, -MaxPushSpeed, MaxPushSpeed), Mathf.Clamp(push.y, -MaxPushSpeed, MaxPushSpeed), 0f);
			foreach (BasePart part in contraption.Parts)
			{
				if (part != null && part.rigidbody != null && !part.rigidbody.isKinematic)
				{
					part.rigidbody.AddForce(deltaV, ForceMode.VelocityChange);
				}
			}
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
			else if (subType == MsgPush)
			{
				int targetId = reader.ReadInt();
				Vector2 push = new Vector2(reader.ReadFloat(), reader.ReadFloat());
				if (SolidContraptions && LocalPlayer != null && targetId == LocalPlayer.Id)
				{
					ApplyPush(push);
				}
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
				bool solid = GUILayout.Toggle(SolidContraptions, " Collisions between players", ui.ToggleStyle);
				if (solid != SolidContraptions)
				{
					SetSolid(solid);
				}
			}
			else
			{
				GUILayout.Label(SolidContraptions ? "Collisions: on" : "Collisions: off (host decides)", ui.Label);
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
